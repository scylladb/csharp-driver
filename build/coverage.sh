#!/usr/bin/env bash
# Measures the code coverage of the code the driver ships -- the three packaged
# assemblies, ScyllaDB (src/Cassandra), Cassandra.AppMetrics and
# Cassandra.OpenTelemetry -- as exercised by the test suites, and writes one
# Cobertura report per suite:
#
#   coverage/unit.cobertura.xml         src/Cassandra.Tests, through `make test-unit`
#   coverage/integration.cobertura.xml  src/Cassandra.IntegrationTests against a
#                                       ScyllaDB cluster and Simulacron, through
#                                       `make test-integration-scylla`
#
# Both suites run through the Makefile targets the regular CI lanes run, with
# coverage collection added by TEST_COVERAGE_OPTIONS, so the set of tests
# measured here cannot drift from the set CI runs. The ScyllaDB version is the
# Makefile's SCYLLA_VERSION, and the test filter is its
# TEST_INTEGRATION_SCYLLA_FILTER; neither is restated here.
#
# --unit-only runs the unit suite alone, for a machine without CCM or a JDK; CI
# never passes it. The Apache Cassandra lane is not measured: this lane measures
# against the one server the driver is built for, so code that only a Cassandra
# server reaches counts as uncovered, alike on every run. See "Code coverage" in
# CONTRIBUTING.md for the rest of what is left out and why.
#
# What coverage collection changes: nothing in the build. Both suites build the
# same Release configuration into the same bin/ and obj/ directories as a
# regular `make test-unit` or `make test-integration-scylla`; coverlet
# instruments the copies of the driver assemblies in each test project's output
# directory for the length of the run and restores them when it ends. The only
# output this script adds is coverage/.
#
# Deliberately not `set -e`: a failing suite must not keep the suites after it
# from running, or a broken test leaves no coverage to diagnose it with. Each
# suite records its failure in $status instead, and the script exits with that
# status once every suite has run.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
shopt -s nullglob

mode=all
if [[ $# -eq 1 && $1 == --unit-only ]]; then
    mode=unit-only
elif [[ $# -ne 0 ]]; then
    echo "usage: $0 [--unit-only]" >&2
    exit 2
fi

# Check the tools before anything is touched, the last run's reports included,
# rather than leave the run to fail a suite at a time.
if ! dotnet --version >/dev/null 2>&1; then
    echo "error: the .NET SDK is missing, or is not the one global.json pins: see \"Prerequisites\" in CONTRIBUTING.md" >&2
    exit 1
fi
if [[ $mode == all ]] && ! java -version >/dev/null 2>&1; then
    echo "error: java is missing: CCM and Simulacron need a JDK, see \"Required tools\" in CONTRIBUTING.md" >&2
    exit 1
fi

rm -rf coverage || exit 1
mkdir -p coverage || exit 1
: >coverage/summary.txt || exit 1

# The files git tracks: the list Codecov matches the paths in a report against.
# A path it cannot match to one of them is dropped from the report, silently and
# file by file, so a report that names anything else is a failure here.
declare -A tracked=()
if tracked_files=$(git ls-files 2>/dev/null) && [[ -n $tracked_files ]]; then
    while IFS= read -r tracked_file; do
        tracked[$tracked_file]=1
    done <<<"$tracked_files"
else
    echo "note: not a git checkout: the check that a report names tracked files only is skipped"
fi

# attr NAME FILE: the value of a numeric attribute of the report's root element.
attr() {
    grep -o "$1=\"[0-9]*\"" "$2" | head -n 1 | tr -dc '0-9'
}

# check_report SUITE REPORT: fails unless the report measures the shipped code.
check_report() {
    local suite=$1 report=$2
    local valid covered source prefix root name missing=0

    valid=$(attr lines-valid "$report")
    covered=$(attr lines-covered "$report")
    if [[ -z $valid || $valid -eq 0 ]]; then
        echo "error: $suite: $report counts no line: the <Include> filter in build/coverage.runsettings matched none of the shipped assemblies" >&2
        return 1
    fi
    if [[ -z $covered || $covered -eq 0 ]]; then
        echo "error: $suite: $report counts no covered line: the suite ran without the instrumented assemblies" >&2
        return 1
    fi

    # The report names each file relative to its <source>, which is the absolute
    # path of src/ in this checkout. A deterministic build would name them
    # relative to /_/ instead, and Codecov would match none of them.
    source=$(sed -n 's#.*<source>\(.*\)</source>.*#\1#p' "$report" | head -n 1)
    prefix=${source%/}/
    # Whether the path the compiler recorded is the logical or the resolved one
    # depends on how this checkout was reached, so strip either.
    for root in "$PWD" "$(pwd -P)"; do
        prefix=${prefix#"$root"/}
    done
    if [[ -z $source || $prefix == /* ]]; then
        echo "error: $suite: $report names its sources relative to '$source', which is not a directory of this checkout" >&2
        return 1
    fi

    ((${#tracked[@]})) || return 0
    while IFS= read -r name; do
        [[ -n ${tracked[$prefix$name]+set} ]] && continue
        echo "error: $suite: $report names $prefix$name, which this checkout does not track: Codecov drops a path it cannot match" >&2
        missing=$((missing + 1))
        ((missing >= 5)) && break
    done < <(grep -o 'filename="[^"]*"' "$report" | sed 's/^filename="//; s/"$//' | sort -u)
    ((missing == 0))
}

# summarize SUITE REPORT: one line of figures, for the job summary.
summarize() {
    awk -v suite="$1" \
        -v lc="$(attr lines-covered "$2")" -v lv="$(attr lines-valid "$2")" \
        -v bc="$(attr branches-covered "$2")" -v bv="$(attr branches-valid "$2")" \
        'BEGIN { printf "%-12s lines %d/%d (%.2f%%)  branches %d/%d (%.2f%%)\n",
                 suite, lc, lv, lv ? 100 * lc / lv : 0, bc, bv, bv ? 100 * bc / bv : 0 }'
}

status=0

# run_suite NAME TARGET: runs one Makefile test target with coverage collection
# and leaves its report at coverage/NAME.cobertura.xml.
run_suite() {
    local suite=$1 target=$2
    local results=coverage/results/$suite report=coverage/$suite.cobertura.xml
    local -a produced

    echo "=== coverage: $suite (make $target)"
    make "$target" \
        TEST_COVERAGE_OPTIONS="--settings build/coverage.runsettings --results-directory $results" ||
        status=1

    # The collector writes its report into a directory of its own named after
    # the test run, so there is exactly one; none means the suite produced no
    # coverage at all, which `make` alone does not report.
    produced=("$results"/*/coverage.cobertura.xml)
    if ((${#produced[@]} != 1)); then
        echo "error: $suite: ${#produced[@]} coverage reports under $results/, expected one" >&2
        status=1
        return
    fi
    mv "${produced[0]}" "$report" || {
        status=1
        return
    }
    check_report "$suite" "$report" || status=1
    summarize "$suite" "$report" >>coverage/summary.txt
}

run_suite unit test-unit
if [[ $mode == unit-only ]]; then
    echo "--unit-only: the integration suite is skipped; coverage/ holds the unit suite only."
else
    run_suite integration test-integration-scylla
fi

cat coverage/summary.txt
exit "$status"
