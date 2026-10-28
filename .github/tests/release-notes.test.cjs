// Copyright ScyllaDB, Inc. SPDX-License-Identifier: Apache-2.0
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {join} = require('node:path');
const {test} = require('node:test');

const workflow = readFileSync(join(__dirname, '../workflows/label-release-notes.yml'), 'utf8');
// Execute the workflow's inline scripts without installing a YAML parser.
const scripts = [...workflow.matchAll(/^          script: \|\n((?:^ {12}.*\n|^\n)*)/gm)]
  .map(match => match[1].replace(/^ {12}/gm, ''));
const conditions = [...workflow.matchAll(/^        if: (.+)$/gm)].map(match => match[1]);
assert.equal(scripts.length, 2);
assert.equal(conditions.length, 2);
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const repo = {owner: 'scylladb', repo: 'csharp-driver'};
const label = 'omit-from-release-notes';

async function classify(options = {}) {
  const files = options.files ?? [{filename: 'README.md', status: 'modified'}];
  const labels = new Set(options.labels ?? [label, 'unrelated']);
  const calls = [];
  const errors = [];
  const steps = {target: {outputs: {}}, labeler: {outcome: 'skipped', outputs: {}}};
  const github = {rest: {pulls: {
    get: async params => {
      assert.deepEqual(params, {...repo, pull_number: 20});
      calls.push('get');
      if (options.lookupError) throw options.lookupError;
      return {data: {number: 20, changed_files: files.length, ...options.pr}};
    },
    listFiles: () => {}
  }, issues: {
    removeLabel: async params => {
      assert.deepEqual(params, {...repo, issue_number: 20, name: label});
      calls.push('remove');
      if (options.cleanupError) throw options.cleanupError;
      if (!labels.delete(label)) throw {status: 404};
    }
  }}, paginate: async (method, params) => {
    assert.equal(method, github.rest.pulls.listFiles);
    assert.deepEqual(params, {...repo, pull_number: 20, per_page: 100});
    calls.push('files');
    if (options.fileError) throw options.fileError;
    return files;
  }};
  const context = {repo, eventName: 'workflow_dispatch', ...options.context};
  const core = {
    setOutput: (key, value) => {steps.target.outputs[key] = value;},
    setFailed: message => {errors.push(new Error(message));},
    info: () => {}
  };
  const execute = async script => {
    try {
      await new AsyncFunction('github', 'context', 'core', 'process', script)(
        github, context, core, {env: {
          PR_NUMBER: options.number ?? '20',
          LABELER_OUTCOME: steps.labeler.outcome,
          LABELER_ELIGIBLE: steps.target.outputs.eligible,
          LABELER_OUTPUTS: JSON.stringify(steps.labeler.outputs)
        }});
    } catch (error) {errors.push(error);}
  };
  const enabled = condition => new Function('steps', 'success', 'always', `return ${condition}`)(
    steps, () => errors.length === 0, () => true);
  await execute(scripts[0]);
  if (enabled(conditions[0])) {
    calls.push('labeler');
    steps.labeler.outcome = options.labelerOutcome ?? 'success';
    steps.labeler.outputs = options.labelerOutputs ?? {'all-labels': [...labels].join(',')};
    if (steps.labeler.outcome === 'failure') errors.push(new Error('Labeler failed'));
  }
  if (enabled(conditions[1])) await execute(scripts[1]);
  return {labels: [...labels], calls, errors, outputs: steps.target.outputs};
}

test('complete listings reach labeler without a second classifier or label lookup', async () => {
  for (const files of [
    [{filename: 'README.md', status: 'modified'}],
    [{filename: 'src/Cassandra/Cluster.cs', status: 'added'}],
    Array.from({length: 105}, (_, i) => ({filename: `docs/${i}.md`, status: 'removed'}))
  ]) {
    const result = await classify({files});
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.calls, ['get', 'files', 'labeler']);
    assert.deepEqual(result.labels, [label, 'unrelated']);
  }
});

test('successful classification with an empty label output stays successful without cleanup', async () => {
  const result = await classify({files: [{filename: 'src/Cassandra/Cluster.cs', status: 'modified'}],
    labels: [], labelerOutputs: {'all-labels': ''}});
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.calls, ['get', 'files', 'labeler']);
  assert.deepEqual(result.labels, []);
});

test('labeler internal skips fail visibly and remove stale omissions', async () => {
  // Both a caught PR lookup failure and an empty file list skip the action's PR loop.
  const result = await classify({labelerOutputs: {}});
  assert.equal(result.errors.length, 1);
  assert.match(result.errors[0].message, /did not report completed classification/);
  assert.deepEqual(result.calls, ['get', 'files', 'labeler', 'remove']);
  assert.deepEqual(result.labels, ['unrelated']);
});

test('all renames stay visible, including support-only renames and mixed changes', async () => {
  for (const previous of ['docs/old.md', 'src/Cassandra/Cluster.cs']) {
    const result = await classify({files: [
      {filename: 'docs/new.md', status: 'renamed', previous_filename: previous},
      {filename: 'README.md', status: 'modified'}
    ]});
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.calls, ['get', 'files', 'remove']);
    assert.deepEqual(result.labels, ['unrelated']);
  }
  const missingOrigin = await classify({files: [{filename: 'docs/new.md', status: 'renamed'}]});
  assert.deepEqual(missingOrigin.errors, []);
  assert.equal(missingOrigin.outputs.eligible, 'false');
  assert.deepEqual(missingOrigin.labels, ['unrelated']);
});

test('empty or incomplete listings fail and remove stale omissions', async () => {
  for (const options of [{files: []}, {pr: {changed_files: 2}}, {pr: {changed_files: 3001}},
    {pr: {changed_files: '1'}}, {pr: {changed_files: -1}}]) {
    const result = await classify(options);
    assert.match(result.errors[0].message, /positive expected count/);
    assert.deepEqual(result.calls, ['get', 'files', 'remove']);
    assert.deepEqual(result.labels, ['unrelated']);
  }
});

test('file retrieval failure cleans up after confirmation and preserves the error', async () => {
  const error = new Error('Files unavailable');
  const result = await classify({fileError: error});
  assert.deepEqual(result.errors, [error]);
  assert.deepEqual(result.labels, ['unrelated']);
});

test('failed, cancelled or skipped labeling removes stale omissions', async () => {
  for (const labelerOutcome of ['failure', 'cancelled', 'skipped']) {
    const result = await classify({labelerOutcome});
    assert.deepEqual(result.calls, ['get', 'files', 'labeler', 'remove']);
    assert.deepEqual(result.labels, ['unrelated']);
    if (labelerOutcome === 'failure') assert.match(result.errors[0].message, /Labeler failed/);
  }
});

test('invalid input never reaches GitHub or changes labels', async () => {
  for (const number of ['', '0', '-1', '01', '1.0', '1e2', ' 20', '20\n', '9007199254740992']) {
    const result = await classify({number});
    assert.match(result.errors[0].message, /canonical positive safe PR number/);
    assert.deepEqual(result.calls, []);
    assert.deepEqual(result.labels, [label, 'unrelated']);
  }
});

test('ordinary issues, mismatched targets and unconfirmed API failures receive no label writes', async () => {
  for (const options of [{lookupError: {status: 404}}, {lookupError: new Error('Unavailable')},
    {pr: {number: 21}}]) {
    const result = await classify(options);
    assert.equal(result.errors.length, 1);
    assert.deepEqual(result.calls, ['get']);
    assert.deepEqual(result.labels, [label, 'unrelated']);
  }
});

test('trusted PR events allow cleanup when the PR lookup fails', async () => {
  const result = await classify({lookupError: new Error('Unavailable'),
    context: {eventName: 'pull_request_target', payload: {pull_request: {number: 20}}}});
  assert.match(result.errors[0].message, /Unavailable/);
  assert.deepEqual(result.calls, ['get', 'remove']);
  assert.deepEqual(result.labels, ['unrelated']);
});

test('cleanup tolerates absent labels and exposes API failures alongside the original error', async () => {
  const absent = await classify({labelerOutcome: 'failure', labels: ['unrelated']});
  assert.equal(absent.errors.length, 1);
  const cleanupError = new Error('Removal unavailable');
  const failed = await classify({labelerOutcome: 'failure', cleanupError});
  assert.match(failed.errors[0].message, /Labeler failed/);
  assert.equal(failed.errors[1], cleanupError);
  const incomplete = await classify({labelerOutputs: {}, cleanupError});
  assert.equal(incomplete.errors.length, 2);
  assert.match(incomplete.errors[0].message, /did not report completed classification/);
  assert.equal(incomplete.errors[1], cleanupError);
  assert.deepEqual(incomplete.calls, ['get', 'files', 'labeler', 'remove']);
});

test('Jira caller excludes only whole-marker add/remove events', () => {
  const jira = readFileSync(join(__dirname, '../workflows/call_jira_sync.yml'), 'utf8');
  const expression = jira.match(/^    if: \$\{\{ (.+) \}\}$/m)[1];
  const allowed = new Function('github', `return ${expression}`);
  for (const action of ['labeled', 'unlabeled']) {
    for (const name of [label, 'Omit-from-release-notes', 'OMIT-FROM-RELEASE-NOTES']) {
      // GitHub expression string equality is case-insensitive.
      assert.equal(allowed({event: {action, label: {name: name.toLowerCase()}}}), false);
    }
    for (const name of ['unrelated', `${label}-extra`, `prefix-${label}`, '']) {
      assert.equal(allowed({event: {action, label: {name}}}), true);
    }
  }
  for (const action of ['opened', 'edited', 'ready_for_review', 'review_requested', 'closed']) {
    assert.equal(allowed({event: {action}}), true);
  }
});
