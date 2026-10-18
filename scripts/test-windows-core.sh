#!/usr/bin/env sh
# Portable tests of the actual native semantic/runner/worker sources, not UIA tests.
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_dir"
command -v mcs >/dev/null 2>&1 || { echo 'Mono mcs is required for the portable C# tests.' >&2; exit 1; }
command -v mono >/dev/null 2>&1 || { echo 'Mono runtime is required for the portable C# tests.' >&2; exit 1; }
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/jev-core-XXXXXX")
trap 'rm -rf "$test_dir"' EXIT HUP INT TERM
mcs -langversion:5 -define:CORE_TESTS -r:System.Core -r:System.Drawing -r:System.Web.Extensions \
  -out:"$test_dir/JevCoreTests.exe" \
  windows/GuideEngine.cs windows/DesktopEngineTests.cs windows/PracticeTask.cs \
  windows/SemanticSkills.cs windows/SemanticSkillTests.cs \
  windows/UiaWorker.cs windows/UiaWorkerTests.cs windows/CoreTestProgram.cs
mono "$test_dir/JevCoreTests.exe"
