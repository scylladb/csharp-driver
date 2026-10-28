#!/usr/bin/env bash
# Tests the control flow of build/coverage.sh: which steps it runs, in which
# order and with which exit status, when a tool is missing, a suite fails, or a
# suite leaves a report that Codecov would not be able to use. It runs a copy of
# the script in a repository of its own, with every tool it calls -- dotnet,
# java, make and git -- replaced by a stub on PATH, so nothing is built and no
# test runs. CI runs this before the real coverage run.
set -euo pipefail

script=$(cd "$(dirname "$0")" && pwd)/coverage.sh
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
repo=$work/repo
failures=0

all_steps="dotnet java test-unit test-integration-scylla"
unit_steps="dotnet test-unit"

# The files the stubbed `git ls-files` reports, and the sources the stubbed
# reports name, relative to the <source> they carry.
tracked=(src/Cassandra/A.cs src/Cassandra/B.cs src/Extensions/Cassandra.AppMetrics/C.cs build/coverage.sh)
measured=(Cassandra/A.cs Cassandra/B.cs Extensions/Cassandra.AppMetrics/C.cs)

# Each stub appends the name of its step to $STUB_STEPS and fails the step if
# STUB_FAIL names it. The make stub also writes the suite's Cobertura report
# where the collector would, unless STUB_NO_REPORT names the suite; STUB_EMPTY,
# STUB_UNCOVERED, STUB_UNTRACKED and STUB_RELOCATED make it write a report that
# measures no line, covers no line, names a file the repository does not track,
# or places its sources outside the checkout.
stubs() {
    cat >"$work/bin/common" <<'EOF'
step() {
    echo "$1" >>"$STUB_STEPS"
    if [[ " ${STUB_FAIL:-} " == *" $1 "* ]]; then
        exit 1
    fi
}
unexpected() {
    echo "stub: unexpected call: $*" >&2
    echo unexpected >>"$STUB_STEPS"
    exit 99
}
EOF
    cat >"$work/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
. "$(dirname "$0")/common"
[[ $* == --version ]] || unexpected dotnet "$@"
step dotnet
echo 10.0.403
EOF
    cat >"$work/bin/java" <<'EOF'
#!/usr/bin/env bash
. "$(dirname "$0")/common"
[[ $* == -version ]] || unexpected java "$@"
step java
EOF
    cat >"$work/bin/git" <<'EOF'
#!/usr/bin/env bash
. "$(dirname "$0")/common"
[[ $* == ls-files ]] || unexpected git "$@"
[[ -z ${STUB_NO_GIT:-} ]] || exit 128
printf '%s\n' $STUB_TRACKED
EOF
    cat >"$work/bin/make" <<'EOF'
#!/usr/bin/env bash
. "$(dirname "$0")/common"
target=$1
case "$target" in
test-unit) suite=unit ;;
test-integration-scylla) suite=integration ;;
*) unexpected make "$@" ;;
esac
# The coverage options the script must hand to the Makefile, so that the
# measured run uses the committed settings and writes where the script looks.
[[ $# -eq 2 && $2 == "TEST_COVERAGE_OPTIONS=--settings build/coverage.runsettings --results-directory coverage/results/$suite" ]] ||
    unexpected make "$@"
if [[ " ${STUB_NO_REPORT:-} " != *" $suite "* ]]; then
    valid=100 covered=60 source="$PWD/src/" files="$STUB_MEASURED"
    [[ " ${STUB_EMPTY:-} " != *" $suite "* ]] || valid=0
    [[ " ${STUB_UNCOVERED:-} " != *" $suite "* ]] || covered=0
    [[ " ${STUB_UNTRACKED:-} " != *" $suite "* ]] || files="$files Cassandra/obj/Generated.g.cs"
    [[ " ${STUB_RELOCATED:-} " != *" $suite "* ]] || source="/_/src/"
    # The collector names the directory after the test run; the script expects
    # to find exactly one report below the results directory.
    mkdir -p "coverage/results/$suite/42"
    {
        echo "<coverage line-rate=\"0.6\" lines-covered=\"$covered\" lines-valid=\"$valid\" branches-covered=\"5\" branches-valid=\"20\">"
        echo "  <sources><source>$source</source></sources>"
        for file in $files; do
            echo "  <class name=\"X\" filename=\"$file\"><lines/></class>"
        done
        echo "</coverage>"
    } >"coverage/results/$suite/42/coverage.cobertura.xml"
fi
step "$target"
EOF
    chmod +x "$work/bin/dotnet" "$work/bin/java" "$work/bin/git" "$work/bin/make"
}

# A fresh copy of the script, in a repository of its own.
setup() {
    rm -rf "${repo:?}" "${work:?}/bin"
    mkdir -p "$repo/build" "$work/bin"
    cp "$script" "$repo/build/coverage.sh"
    stubs
}

# Runs the copy with the given arguments, and records its exit status in $status
# and the steps it ran in $steps.
run() {
    : >"$work/steps"
    status=0
    STUB_STEPS=$work/steps STUB_TRACKED="${tracked[*]}" STUB_MEASURED="${measured[*]}" \
        PATH="$work/bin:$PATH" \
        bash "$repo/build/coverage.sh" "$@" >"$work/output" 2>&1 || status=$?
    steps=$(tr '\n' ' ' <"$work/steps")
    steps=${steps% }
}

pass() {
    echo "ok: $1"
}

fail() {
    echo "FAILED: $1"
    failures=$((failures + 1))
}

# expect DESCRIPTION STATUS STEPS: checks the last run's exit status and steps.
expect() {
    if [[ $status == "$2" && $steps == "$3" ]]; then
        pass "$1"
    else
        fail "$1"
        echo "  exit status: $status, expected $2"
        echo "  steps:    $steps"
        echo "  expected: $3"
        sed 's/^/  | /' "$work/output"
    fi
}

# expect_output DESCRIPTION TEXT: checks that the last run's output says TEXT.
expect_output() {
    if grep -qF -- "$2" "$work/output"; then
        pass "$1"
    else
        fail "$1"
        sed 's/^/  | /' "$work/output"
    fi
}

# expect_reports DESCRIPTION SUITE...: checks which reports the run left behind.
expect_reports() {
    local description=$1 expected found
    shift
    expected=$(printf '%s\n' "$@" | sort | tr '\n' ' ')
    found=$(find "$repo/coverage" -maxdepth 1 -name '*.cobertura.xml' -printf '%f\n' 2>/dev/null | sed 's/\.cobertura\.xml$//' | sort | tr '\n' ' ')
    if [[ $found == "$expected" ]]; then
        pass "$description"
    else
        fail "$description"
        echo "  found:    $found"
        echo "  expected: $expected"
    fi
}

setup
run
expect "a clean run checks its tools, runs both suites and succeeds" 0 "$all_steps"
expect_reports "... and leaves one report per suite" unit integration
expect_output "... and reports each suite's figures" "unit         lines 60/100 (60.00%)"

setup
run --unit-only
expect "--unit-only runs the unit suite only, and does not need a JDK" 0 "$unit_steps"
expect_reports "... and leaves the unit report only" unit
expect_output "... and says the integration suite was skipped" "the integration suite is skipped"

setup
for args in "--unit-only --integration" --all --bogus; do
    # shellcheck disable=SC2086 # split into arguments on purpose
    run $args
    expect "arguments $args are rejected before anything runs" 2 ""
done

setup
STUB_FAIL=dotnet run
expect "a missing .NET SDK stops the run first" 1 "dotnet"
expect_output "... and says where the prerequisites are" '"Prerequisites" in CONTRIBUTING.md'
setup
STUB_FAIL=java run
expect "a missing JDK stops the run before any suite" 1 "dotnet java"
expect_output "... and says what needs it" "CCM and Simulacron need a JDK"

for suite in test-unit test-integration-scylla; do
    setup
    STUB_FAIL=$suite run
    expect "a failing $suite still runs the suites after it, and fails the run" 1 "$all_steps"
done
setup
STUB_FAIL=test-unit run
expect_reports "... and keeps the report the failing suite still wrote" integration unit

setup
STUB_NO_REPORT=unit run
expect "a suite that writes no report fails the run, after the other suite" 1 "$all_steps"
expect_output "... and says so" "0 coverage reports under coverage/results/unit/, expected one"

setup
STUB_EMPTY=integration run
expect "a report that counts no line fails the run" 1 "$all_steps"
expect_output "... and blames the include filter" "matched none of the shipped assemblies"

setup
STUB_UNCOVERED=integration run
expect "a report that counts no covered line fails the run" 1 "$all_steps"
expect_output "... and blames the instrumentation" "ran without the instrumented assemblies"

setup
STUB_UNTRACKED=unit run
expect "a report naming a file the repository does not track fails the run" 1 "$all_steps"
expect_output "... and names the file" "names src/Cassandra/obj/Generated.g.cs, which this checkout does not track"

setup
STUB_RELOCATED=unit run
expect "a report whose sources lie outside the checkout fails the run" 1 "$all_steps"
expect_output "... and says where they point" "relative to '/_/src/'"

setup
STUB_NO_GIT=1 STUB_UNTRACKED=unit run
expect "outside a git checkout the run cannot check the paths, and says so" 0 "$all_steps"
expect_output "... and says why" "not a git checkout"

if ((failures)); then
    echo "$failures failed"
    exit 1
fi
echo "all passed"
