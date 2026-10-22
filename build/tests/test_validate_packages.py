import importlib.util
import itertools
import json
import os
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path
from unittest import mock


MODULE_PATH = Path(__file__).resolve().parents[1] / "validate-packages.py"
MODULE_SPEC = importlib.util.spec_from_file_location("validate_packages", MODULE_PATH)
validate_packages = importlib.util.module_from_spec(MODULE_SPEC)
sys.modules[MODULE_SPEC.name] = validate_packages
MODULE_SPEC.loader.exec_module(validate_packages)


class ValidatePackagesTests(unittest.TestCase):
    def test_sdk_validation_uses_the_repository_sdk_policy(self):
        expected = json.loads(
            (validate_packages.REPOSITORY_ROOT / "global.json").read_text(encoding="utf-8")
        )["sdk"]["version"]
        for version in (expected, "0.0.0"):
            with self.subTest(version=version), mock.patch.object(
                validate_packages,
                "run",
                return_value=subprocess.CompletedProcess([], 0, stdout=version + "\n"),
            ):
                if version == expected:
                    validate_packages.validate_sdk()
                else:
                    with self.assertRaisesRegex(
                        validate_packages.ValidationError, "Expected .NET SDK"
                    ):
                        validate_packages.validate_sdk()

    def test_dependency_contracts_match_shipping_project_references(self):
        package_ids = {
            contract.package_id for contract in validate_packages.PACKAGE_CONTRACTS
        }
        for contract in validate_packages.PACKAGE_CONTRACTS:
            with self.subTest(package_id=contract.package_id):
                project = validate_packages.ElementTree.parse(
                    validate_packages.REPOSITORY_ROOT / contract.project
                ).getroot()
                expected = {
                    reference.attrib["Include"]: reference.attrib["Version"]
                    for reference in project.findall(".//PackageReference")
                }
                self.assertEqual(
                    expected,
                    {
                        package_id: version
                        for package_id, version in contract.dependencies.items()
                        if package_id not in package_ids
                    },
                )

    def test_run_merges_environment_overrides(self):
        completed = subprocess.CompletedProcess(["command"], 0)
        with mock.patch.dict(os.environ, {"VALIDATOR_PARENT": "preserved"}):
            with mock.patch.object(
                validate_packages.subprocess, "run", return_value=completed
            ) as subprocess_run:
                validate_packages.run(
                    ["command"], environment={"VALIDATOR_OVERRIDE": "applied"}
                )

        environment = subprocess_run.call_args.kwargs["env"]
        self.assertEqual("preserved", environment["VALIDATOR_PARENT"])
        self.assertEqual("applied", environment["VALIDATOR_OVERRIDE"])

    def test_run_keeps_stderr_separate_from_captured_stdout_by_default(self):
        completed = subprocess.CompletedProcess(["command"], 0, stdout="output")
        with mock.patch.object(
            validate_packages.subprocess, "run", return_value=completed
        ) as subprocess_run:
            validate_packages.run(["command"], capture_output=True)

        self.assertEqual(subprocess.PIPE, subprocess_run.call_args.kwargs["stdout"])
        self.assertIsNone(subprocess_run.call_args.kwargs["stderr"])

    def test_run_can_merge_stderr_into_captured_stdout(self):
        completed = subprocess.CompletedProcess(["command"], 0, stdout="output")
        with mock.patch.object(
            validate_packages.subprocess, "run", return_value=completed
        ) as subprocess_run:
            validate_packages.run(
                ["command"], capture_output=True, merge_stderr=True
            )

        self.assertEqual(subprocess.PIPE, subprocess_run.call_args.kwargs["stdout"])
        self.assertEqual(subprocess.STDOUT, subprocess_run.call_args.kwargs["stderr"])

    def test_reference_pack_comes_from_msbuild_resolution(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            packs = Path(temporary_directory) / "packs/Microsoft.NETCore.App.Ref"
            expected = packs / "10.0.12/ref/net10.0"
            expected.mkdir(parents=True)
            (packs / "10.0.13/ref/net10.0").mkdir(parents=True)
            (packs / "10.0.0-preview.7.25380.108/ref/net10.0").mkdir(
                parents=True
            )
            output = {
                "Items": {
                    "ResolvedTargetingPack": [
                        {
                            "NuGetPackageId": "Microsoft.NETCore.App.Ref",
                            "TargetFramework": "net10.0",
                            "PackageDirectory": str(packs / "10.0.12"),
                        },
                        {
                            "NuGetPackageId": "Microsoft.AspNetCore.App.Ref",
                            "TargetFramework": "net10.0",
                            "PackageDirectory": str(
                                Path(temporary_directory)
                                / "packs/Microsoft.AspNetCore.App.Ref/10.0.12"
                            ),
                        },
                    ]
                }
            }
            completed = subprocess.CompletedProcess(
                ["dotnet", "msbuild"], 0, stdout=json.dumps(output)
            )

            with mock.patch.object(validate_packages, "DOTNET", "/selected/dotnet"):
                with mock.patch.object(
                    validate_packages, "run", return_value=completed
                ) as run:
                    actual = validate_packages.net10_reference_pack()

            self.assertEqual(expected, actual)
            command = run.call_args.args[0]
            self.assertEqual("/selected/dotnet", command[0])
            self.assertIn("-target:ResolveFrameworkReferences", command)
            self.assertIn("-getItem:ResolvedTargetingPack", command)

    def test_api_compat_uses_selected_host_and_invariant_globalization(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            tool_assembly = self._create_tool_assembly(work_directory)
            completed = subprocess.CompletedProcess([], 0, stdout="")

            with self._isolated_repository(work_directory), mock.patch.object(
                validate_packages, "DOTNET", "/selected/dotnet"
            ):
                with mock.patch.object(
                    validate_packages, "run", return_value=completed
                ) as run:
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                    )

            self.assertEqual(4, run.call_count)
            for call in run.call_args_list[1:]:
                command = call.args[0]
                self.assertEqual(
                    ["/selected/dotnet", "exec", str(tool_assembly)], command[:3]
                )
                self.assertEqual(
                    {"DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1"},
                    call.kwargs["environment"],
                )
                self.assertTrue(call.kwargs["merge_stderr"])

    def test_api_compat_uses_assembly_paths_without_the_work_directory(self):
        for dry_run, update_suppressions in itertools.product((False, True), repeat=2):
            with self.subTest(dry_run=dry_run, update_suppressions=update_suppressions):
                with tempfile.TemporaryDirectory() as temporary_directory:
                    work_directory = Path(temporary_directory)
                    self._create_tool_assembly(work_directory)
                    completed = subprocess.CompletedProcess([], 0, stdout="")

                    with self._isolated_repository(work_directory), mock.patch.object(
                        validate_packages, "run", return_value=completed
                    ) as run:
                        validate_packages.run_api_compat(
                            work_directory,
                            {
                                package_id: work_directory / "baselines" / path
                                for package_id, path in self._assemblies("lib").items()
                            },
                            self._candidate_assemblies(work_directory, dry_run),
                            work_directory / "left-references",
                            work_directory / "right-references",
                            update_suppressions=update_suppressions,
                        )

                    # Suppressions record these paths, so a dry run, which extracts
                    # the packages under their .DRYRUN IDs, must compare the same ones
                    for contract, call in zip(
                        validate_packages.PACKAGE_CONTRACTS, run.call_args_list[1:]
                    ):
                        command = call.args[0]
                        left = command[command.index("--left-assembly") + 1]
                        right = command[command.index("--right-assembly") + 1]
                        self.assertEqual(
                            f"baselines/lib/{contract.assembly_name}.dll", left
                        )
                        self.assertEqual(
                            f"candidate-extracted/{contract.package_id}/lib/net10.0/"
                            f"{contract.assembly_name}.dll",
                            right,
                        )
                        self.assertTrue((work_directory / right).is_file())
                        self.assertEqual(work_directory, call.kwargs["cwd"])

    def test_api_compat_passes_existing_suppression_files(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            self._create_tool_assembly(work_directory)
            repository = work_directory / "repository"
            core = validate_packages.PACKAGE_CONTRACTS[0]
            suppressions = (
                repository / core.project
            ).parent / validate_packages.API_COMPAT_SUPPRESSIONS_FILE
            suppressions.parent.mkdir(parents=True)
            suppressions.write_text("<Suppressions />", encoding="utf-8")
            completed = subprocess.CompletedProcess([], 0, stdout="")

            with mock.patch.object(validate_packages, "REPOSITORY_ROOT", repository):
                with mock.patch.object(
                    validate_packages, "run", return_value=completed
                ) as run:
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                    )

            commands = [call.args[0] for call in run.call_args_list[1:]]
            self.assertEqual(
                ["--suppression-file", str(suppressions)], commands[0][-2:]
            )
            for command in commands[1:]:
                self.assertNotIn("--suppression-file", command)
                self.assertNotIn("--generate-suppression-file", command)

    def test_api_compat_regenerates_suppression_files_when_requested(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            self._create_tool_assembly(work_directory)
            repository = work_directory / "repository"
            completed = subprocess.CompletedProcess([], 0, stdout="")

            with mock.patch.object(validate_packages, "REPOSITORY_ROOT", repository):
                with mock.patch.object(
                    validate_packages, "run", return_value=completed
                ) as run:
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                        update_suppressions=True,
                    )

            for contract, call in zip(
                validate_packages.PACKAGE_CONTRACTS, run.call_args_list[1:]
            ):
                output = Path(call.args[0][-1])
                self.assertEqual(
                    [
                        "--generate-suppression-file",
                        "--suppression-output-file",
                    ],
                    call.args[0][-3:-1],
                )
                self.assertEqual(
                    (repository / contract.project).parent, output.parent.parent
                )
                self.assertEqual(
                    validate_packages.API_COMPAT_SUPPRESSIONS_FILE, output.name
                )
                self.assertFalse(output.exists())

    def test_api_compat_removes_stale_suppression_files_after_successful_generation(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            self._create_tool_assembly(work_directory)
            repository = work_directory / "repository"
            core = validate_packages.PACKAGE_CONTRACTS[0]
            suppressions = (
                repository / core.project
            ).parent / validate_packages.API_COMPAT_SUPPRESSIONS_FILE
            suppressions.parent.mkdir(parents=True)
            suppressions.write_text("<Suppressions />", encoding="utf-8")
            # Like ApiCompat for a package without differences, write no file
            completed = subprocess.CompletedProcess([], 0, stdout="")

            with mock.patch.object(validate_packages, "REPOSITORY_ROOT", repository):
                with mock.patch.object(validate_packages, "run", return_value=completed):
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                        update_suppressions=True,
                    )

            self.assertFalse(suppressions.exists())

    def test_api_compat_replaces_reviewed_suppressions_after_successful_generation(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            self._create_tool_assembly(work_directory)
            with self._isolated_repository(work_directory):
                suppressions = validate_packages.api_compat_suppressions_file(
                    validate_packages.PACKAGE_CONTRACTS[0]
                )
                suppressions.parent.mkdir(parents=True)
                original = self._suppressions_xml(["T:Cassandra.PreviousBreak"]).encode()
                replacement = self._suppressions_xml(["T:Cassandra.NewBreak"]).encode()
                suppressions.write_bytes(original)

                def generate(command, **_):
                    if "--generate-suppression-file" in command:
                        output = Path(command[command.index("--suppression-output-file") + 1])
                        if output.parent.parent == suppressions.parent:
                            self.assertEqual(original, suppressions.read_bytes())
                            output.write_bytes(replacement)
                    return subprocess.CompletedProcess([], 0, stdout="")

                with mock.patch.object(validate_packages, "run", side_effect=generate):
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                        update_suppressions=True,
                    )

                self.assertEqual(replacement, suppressions.read_bytes())
                self.assertEqual([suppressions], list(suppressions.parent.iterdir()))

    def test_api_compat_preserves_reviewed_suppressions_when_generation_fails(self):
        failures = (
            (1, "", None, "could not record the differences"),
            (
                0, "Could not resolve reference 'System.Runtime.dll'.", None,
                "could not resolve all references",
            ),
            (0, "", subprocess.TimeoutExpired("apicompat", 60), "timed out"),
        )
        for returncode, stdout, exception, message in failures:
            for partial_output in (False, True):
                with self.subTest(message=message, partial_output=partial_output):
                    with tempfile.TemporaryDirectory() as temporary_directory:
                        work_directory = Path(temporary_directory)
                        self._create_tool_assembly(work_directory)
                        with self._isolated_repository(work_directory):
                            suppressions = validate_packages.api_compat_suppressions_file(
                                validate_packages.PACKAGE_CONTRACTS[0]
                            )
                            suppressions.parent.mkdir(parents=True)
                            original = self._suppressions_xml(["T:Cassandra.Reviewed"]).encode()
                            suppressions.write_bytes(original)

                            def fail_generation(command, **_):
                                if "--generate-suppression-file" in command:
                                    if partial_output:
                                        output = Path(
                                            command[command.index("--suppression-output-file") + 1]
                                        )
                                        output.write_bytes(b"<partial")
                                    if exception:
                                        raise exception
                                    return subprocess.CompletedProcess(
                                        [], returncode, stdout=stdout
                                    )
                                return subprocess.CompletedProcess([], 0, stdout="")

                            with mock.patch.object(
                                validate_packages, "run", side_effect=fail_generation
                            ), self.assertRaisesRegex(
                                type(exception)
                                if exception else validate_packages.ValidationError,
                                message,
                            ):
                                validate_packages.run_api_compat(
                                    work_directory,
                                    self._assemblies("baseline"),
                                    self._candidate_assemblies(work_directory),
                                    work_directory / "left-references",
                                    work_directory / "right-references",
                                    update_suppressions=True,
                                )

                            self.assertEqual(original, suppressions.read_bytes())
                            self.assertEqual(
                                [suppressions], list(suppressions.parent.iterdir())
                            )

    def test_suppressions_treat_an_omitted_baseline_marker_as_false(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)

            def read(marker):
                path = directory / "suppressions.xml"
                path.write_text(
                    "<Suppressions><Suppression><DiagnosticId>CP0001</DiagnosticId>"
                    "<Target>T:Cassandra.Removed</Target><Left>left.dll</Left>"
                    f"<Right>right.dll</Right>{marker}</Suppression></Suppressions>",
                    encoding="utf-8",
                )
                return validate_packages.read_api_compat_suppressions(path)

            omitted = read("")
            self.assertEqual(
                omitted, read("<IsBaselineSuppression>false</IsBaselineSuppression>")
            )
            self.assertEqual(
                omitted, read("<IsBaselineSuppression> False </IsBaselineSuppression>")
            )
            self.assertNotEqual(
                omitted, read("<IsBaselineSuppression>true</IsBaselineSuppression>")
            )

    def test_api_compat_rejects_suppressions_that_match_no_current_difference(self):
        with self.assertRaisesRegex(
            validate_packages.ValidationError,
            r"declares API breaks that no longer exist \(T:Cassandra.Removed\)",
        ):
            self._run_api_compat_with_suppressions(
                declared=["T:Cassandra.Removed", "T:Cassandra.StillRemoved"],
                current=["T:Cassandra.StillRemoved"],
            )

    def test_api_compat_accepts_suppressions_that_match_the_current_differences(self):
        commands = self._run_api_compat_with_suppressions(
            declared=["T:Cassandra.Removed"], current=["T:Cassandra.Removed"]
        )

        core = commands[0]
        self.assertIn("--suppression-file", core)
        self.assertNotIn("--generate-suppression-file", core)
        self.assertIn("--generate-suppression-file", commands[1])
        # The other packages have no suppression file, so nothing to compare
        self.assertEqual(4, len(commands))

    def test_api_compat_rejects_unresolved_references_with_zero_exit(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            self._create_tool_assembly(work_directory)
            installed = subprocess.CompletedProcess([], 0, stdout="")
            unresolved = subprocess.CompletedProcess(
                [],
                0,
                stdout="Could not resolve reference 'System.Runtime.dll'.\n",
            )

            with self._isolated_repository(work_directory), mock.patch.object(
                validate_packages, "run", side_effect=[installed, unresolved]
            ):
                with self.assertRaisesRegex(
                    validate_packages.ValidationError,
                    "could not resolve all references",
                ):
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                    )

    def test_package_inspection_accepts_the_exact_contract(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            packages = root / "packages"
            self._create_candidate_packages(packages)

            assemblies = validate_packages.inspect_candidates(
                packages, root / "extracted"
            )

            self.assertEqual(
                {
                    contract.package_id
                    for contract in validate_packages.PACKAGE_CONTRACTS
                },
                set(assemblies),
            )

    def test_dry_run_contracts_replace_package_and_internal_dependency_ids(self):
        contracts = validate_packages.candidate_contracts(dry_run=True)

        self.assertEqual(
            [
                "ScyllaDBCSharpDriver.DRYRUN",
                "ScyllaDBCSharpDriver.DRYRUN.AppMetrics",
                "ScyllaDBCSharpDriver.DRYRUN.OpenTelemetry",
            ],
            [contract.package_id for contract in contracts],
        )
        for contract in contracts[1:]:
            self.assertEqual(
                validate_packages.CANDIDATE_VERSION,
                contract.dependencies["ScyllaDBCSharpDriver.DRYRUN"],
            )
            self.assertNotIn("ScyllaDBCSharpDriver", contract.dependencies)

    def test_package_inspection_accepts_dry_run_ids_and_dependencies(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            packages = root / "packages"
            contracts = validate_packages.candidate_contracts(dry_run=True)
            self._create_candidate_packages(packages, contracts=contracts)

            assemblies = validate_packages.inspect_candidates(
                packages, root / "extracted", contracts
            )

            self.assertEqual(
                {contract.package_id for contract in contracts}, set(assemblies)
            )

    def test_dry_run_consumer_files_and_hashes_use_dry_run_ids(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            packages = root / "packages"
            contracts = validate_packages.candidate_contracts(dry_run=True)
            self._create_candidate_packages(packages, contracts=contracts)

            project = root / "Consumer.csproj"
            config = root / "NuGet.Config"
            validate_packages.write_consumer_project(project, "net10.0", contracts)
            validate_packages.write_consumer_nuget_config(
                config, packages, contracts
            )

            project_root = validate_packages.ElementTree.parse(project).getroot()
            references = {
                reference.attrib["Include"]
                for reference in project_root.findall(".//PackageReference")
            }
            config_root = validate_packages.ElementTree.parse(config).getroot()
            patterns = {
                package.attrib["pattern"]
                for package in config_root.findall(
                    ".//packageSource[@key='local-candidates']/package"
                )
            }
            expected_ids = {contract.package_id for contract in contracts}
            self.assertEqual(expected_ids, references)
            self.assertEqual(expected_ids, patterns)

            libraries = {}
            for contract in contracts:
                package = next(
                    packages.glob(
                        f"{contract.package_id}."
                        f"{validate_packages.CANDIDATE_VERSION}.nupkg"
                    )
                )
                package_hash = validate_packages.base64.b64encode(
                    validate_packages.hashlib.sha512(package.read_bytes()).digest()
                ).decode("ascii")
                libraries[
                    f"{contract.package_id}/{validate_packages.CANDIDATE_VERSION}"
                ] = {"sha512": package_hash}
            assets = root / "project.assets.json"
            assets.write_text(json.dumps({"libraries": libraries}), encoding="utf-8")

            validate_packages.assert_local_packages_were_restored(
                assets, packages, contracts
            )

    def test_external_dry_run_packages_are_validated_without_repacking(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            package_directory = Path(temporary_directory) / "staged-packages"
            package_directory.mkdir()
            contracts = validate_packages.candidate_contracts(dry_run=True)
            candidate_assemblies = {
                contract.package_id: Path(f"candidate/{contract.assembly_name}.dll")
                for contract in contracts
            }
            baseline_assemblies = self._assemblies("baseline")

            with (
                mock.patch.object(validate_packages, "validate_sdk"),
                mock.patch.object(validate_packages, "restore_and_audit"),
                mock.patch.object(validate_packages, "ensure_signing_key") as signing,
                mock.patch.object(validate_packages, "pack_candidates") as pack,
                mock.patch.object(
                    validate_packages,
                    "inspect_candidates",
                    return_value=candidate_assemblies,
                ) as inspect,
                mock.patch.object(
                    validate_packages,
                    "download_baselines",
                    return_value=baseline_assemblies,
                ),
                mock.patch.object(
                    validate_packages,
                    "prepare_reference_sets",
                    return_value=(Path("left"), Path("right")),
                ) as prepare,
                mock.patch.object(validate_packages, "run_api_compat") as api_compat,
                mock.patch.object(
                    validate_packages, "validate_consumers"
                ) as consumers,
            ):
                validate_packages.main(
                    [
                        "--package-directory",
                        str(package_directory),
                        "--dry-run",
                    ]
                )

            signing.assert_not_called()
            pack.assert_not_called()
            self.assertEqual(package_directory.resolve(), inspect.call_args.args[0])
            self.assertEqual(contracts, inspect.call_args.args[2])
            canonical_candidates = prepare.call_args.args[2]
            self.assertEqual(
                {contract.package_id for contract in validate_packages.PACKAGE_CONTRACTS},
                set(canonical_candidates),
            )
            for canonical, candidate in zip(
                validate_packages.PACKAGE_CONTRACTS, contracts
            ):
                self.assertEqual(
                    candidate_assemblies[candidate.package_id],
                    canonical_candidates[canonical.package_id],
                )
            self.assertEqual(canonical_candidates, api_compat.call_args.args[2])
            self.assertEqual(package_directory.resolve(), consumers.call_args.args[1])
            self.assertEqual(contracts, consumers.call_args.args[2])

    def test_package_inspection_rejects_dependency_asset_changes(self):
        mutations = (
            ("exclude", "Runtime,Build,Analyzers"),
            ("include", "Compile"),
        )
        for attribute, value in mutations:
            with self.subTest(attribute=attribute):
                with tempfile.TemporaryDirectory() as temporary_directory:
                    root = Path(temporary_directory)
                    packages = root / "packages"

                    def mutate(contract, dependencies):
                        if contract.package_id == "ScyllaDBCSharpDriver":
                            dependencies[0][attribute] = value

                    self._create_candidate_packages(
                        packages, mutate_dependencies=mutate
                    )

                    with self.assertRaisesRegex(
                        validate_packages.ValidationError, "Unexpected dependencies"
                    ):
                        validate_packages.inspect_candidates(
                            packages, root / "extracted"
                        )

    def test_package_inspection_rejects_duplicate_dependencies(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            packages = root / "packages"

            def mutate(contract, dependencies):
                if contract.package_id == "ScyllaDBCSharpDriver":
                    dependencies.append(dependencies[0].copy())

            self._create_candidate_packages(packages, mutate_dependencies=mutate)

            with self.assertRaisesRegex(
                validate_packages.ValidationError, "Unexpected dependencies"
            ):
                validate_packages.inspect_candidates(packages, root / "extracted")

    def test_package_inspection_rejects_ungrouped_dependencies(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            packages = root / "packages"
            self._create_candidate_packages(packages, add_ungrouped_dependency=True)

            with self.assertRaisesRegex(
                validate_packages.ValidationError,
                "exactly one grouped dependency set",
            ):
                validate_packages.inspect_candidates(packages, root / "extracted")

    def test_package_inspection_rejects_undeclared_files(self):
        additions = (
            (
                "ScyllaDBCSharpDriver.AppMetrics",
                "buildMultiTargeting/ScyllaDBCSharpDriver.AppMetrics.targets",
            ),
            ("ScyllaDBCSharpDriver.OpenTelemetry", "unexpected/marker.txt"),
        )
        for package_id, path in additions:
            with self.subTest(path=path):
                with tempfile.TemporaryDirectory() as temporary_directory:
                    root = Path(temporary_directory)
                    packages = root / "packages"
                    self._create_candidate_packages(
                        packages, extra_files={package_id: {path: b"unexpected"}}
                    )

                    with self.assertRaisesRegex(
                        validate_packages.ValidationError,
                        "Unexpected package contents",
                    ):
                        validate_packages.inspect_candidates(
                            packages, root / "extracted"
                        )

    def test_package_inspection_rejects_duplicate_archive_entries(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            packages = root / "packages"
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                self._create_candidate_packages(
                    packages,
                    duplicate_file=("ScyllaDBCSharpDriver", "LICENSE.md"),
                )

            with self.assertRaisesRegex(
                validate_packages.ValidationError, "Duplicate package entries"
            ):
                validate_packages.inspect_candidates(packages, root / "extracted")

    def test_incompatible_consumers_cover_every_package_individually(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)

            def fail_restore(arguments, **kwargs):
                project = Path(arguments[2])
                project_text = project.read_text(encoding="utf-8")
                contract = next(
                    contract
                    for contract in validate_packages.PACKAGE_CONTRACTS
                    if f'Include="{contract.package_id}"' in project_text
                )
                return subprocess.CompletedProcess(
                    arguments,
                    1,
                    stdout=(
                        f"error NU1202: Package {contract.package_id} is not "
                        "compatible "
                        "with net9.0. Package supports: net10.0"
                    ),
                )

            with mock.patch.object(
                validate_packages, "run", side_effect=fail_restore
            ) as run:
                validate_packages.validate_incompatible_consumers(
                    root, root / "NuGet.Config"
                )

            self.assertEqual(len(validate_packages.PACKAGE_CONTRACTS), run.call_count)
            for call in run.call_args_list:
                self.assertTrue(call.kwargs["merge_stderr"])
            for contract in validate_packages.PACKAGE_CONTRACTS:
                project = (
                    root
                    / f"consumer-net9-{contract.package_id}"
                    / "Consumer.csproj"
                )
                project_text = project.read_text(encoding="utf-8")
                self.assertIn(f'Include="{contract.package_id}"', project_text)
                for other_contract in validate_packages.PACKAGE_CONTRACTS:
                    if other_contract != contract:
                        self.assertNotIn(
                            f'Include="{other_contract.package_id}"', project_text
                        )

    def test_incompatible_consumers_use_dry_run_package_ids(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            contracts = validate_packages.candidate_contracts(dry_run=True)

            def fail_restore(arguments, **kwargs):
                project_text = Path(arguments[2]).read_text(encoding="utf-8")
                contract = next(
                    contract
                    for contract in contracts
                    if f'Include="{contract.package_id}"' in project_text
                )
                return subprocess.CompletedProcess(
                    arguments,
                    1,
                    stdout=(
                        f"error NU1202: Package {contract.package_id} is not "
                        "compatible with net9.0. Package supports: net10.0"
                    ),
                )

            with mock.patch.object(
                validate_packages, "run", side_effect=fail_restore
            ):
                validate_packages.validate_incompatible_consumers(
                    root, root / "NuGet.Config", contracts
                )

            for contract in contracts:
                project = (
                    root
                    / f"consumer-net9-{contract.package_id}"
                    / "Consumer.csproj"
                )
                self.assertIn(
                    f'Include="{contract.package_id}"',
                    project.read_text(encoding="utf-8"),
                )

    @staticmethod
    def _isolated_repository(work_directory):
        """Points REPOSITORY_ROOT at an empty directory, so the repository's own suppression files aren't read."""
        return mock.patch.object(
            validate_packages, "REPOSITORY_ROOT", work_directory / "repository"
        )

    @staticmethod
    def _suppressions_xml(targets):
        entries = "".join(
            "<Suppression><DiagnosticId>CP0001</DiagnosticId>"
            f"<Target>{target}</Target><Left>left.dll</Left><Right>right.dll</Right>"
            "</Suppression>"
            for target in targets
        )
        return f'<?xml version="1.0" encoding="utf-8"?><Suppressions>{entries}</Suppressions>'

    def _run_api_compat_with_suppressions(self, declared, current):
        """Runs ApiCompat for a core package that declares some differences, returning the comparison commands.

        The mocked ApiCompat records the current differences when asked to generate a suppression file.
        """
        with tempfile.TemporaryDirectory() as temporary_directory:
            work_directory = Path(temporary_directory)
            self._create_tool_assembly(work_directory)
            repository = work_directory / "repository"
            core = validate_packages.PACKAGE_CONTRACTS[0]
            suppressions = (
                repository / core.project
            ).parent / validate_packages.API_COMPAT_SUPPRESSIONS_FILE
            suppressions.parent.mkdir(parents=True)
            suppressions.write_text(self._suppressions_xml(declared), encoding="utf-8")
            commands = []

            def api_compat(command, **_):
                commands.append(command)
                if "--generate-suppression-file" in command:
                    output = Path(command[command.index("--suppression-output-file") + 1])
                    output.write_text(self._suppressions_xml(current), encoding="utf-8")
                return subprocess.CompletedProcess([], 0, stdout="")

            with mock.patch.object(validate_packages, "REPOSITORY_ROOT", repository):
                with mock.patch.object(validate_packages, "run", side_effect=api_compat):
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._candidate_assemblies(work_directory),
                        work_directory / "left-references",
                        work_directory / "right-references",
                    )
            # The first call installs the tool
            return commands[1:]

    @staticmethod
    def _candidate_assemblies(work_directory, dry_run=False):
        """Creates the candidate assemblies where package inspection extracts them."""
        assemblies = {}
        for canonical, candidate in zip(
            validate_packages.PACKAGE_CONTRACTS,
            validate_packages.candidate_contracts(dry_run),
        ):
            assembly = (
                work_directory
                / validate_packages.CANDIDATE_EXTRACTION_DIRECTORY
                / candidate.package_id
                / f"lib/net10.0/{candidate.assembly_name}.dll"
            )
            assembly.parent.mkdir(parents=True)
            assembly.write_bytes(b"assembly")
            assemblies[canonical.package_id] = assembly
        return assemblies

    @staticmethod
    def _assemblies(directory):
        return {
            contract.package_id: Path(directory) / f"{contract.assembly_name}.dll"
            for contract in validate_packages.PACKAGE_CONTRACTS
        }

    @staticmethod
    def _create_tool_assembly(work_directory):
        tool_assembly = (
            work_directory
            / "tools/.store/microsoft.dotnet.apicompat.tool/10.0.401/"
            "microsoft.dotnet.apicompat.tool/10.0.401/tools/net8.0/any/"
            "Microsoft.DotNet.ApiCompat.Tool.dll"
        )
        tool_assembly.parent.mkdir(parents=True)
        tool_assembly.touch()
        return tool_assembly

    @staticmethod
    def _create_candidate_packages(
        package_directory,
        *,
        contracts=None,
        mutate_dependencies=None,
        extra_files=None,
        duplicate_file=None,
        add_ungrouped_dependency=False,
    ):
        package_directory.mkdir(parents=True)
        extra_files = extra_files or {}
        contracts = contracts or validate_packages.PACKAGE_CONTRACTS

        for contract in contracts:
            dependencies = [
                {
                    "id": dependency_id,
                    "version": version,
                    "exclude": "Build,Analyzers",
                }
                for dependency_id, version in contract.dependencies.items()
            ]
            if mutate_dependencies:
                mutate_dependencies(contract, dependencies)

            package = validate_packages.ElementTree.Element("package")
            metadata = validate_packages.ElementTree.SubElement(package, "metadata")
            validate_packages.ElementTree.SubElement(metadata, "id").text = (
                contract.package_id
            )
            validate_packages.ElementTree.SubElement(metadata, "version").text = (
                validate_packages.CANDIDATE_VERSION
            )
            dependency_groups = validate_packages.ElementTree.SubElement(
                metadata, "dependencies"
            )
            group = validate_packages.ElementTree.SubElement(
                dependency_groups, "group", targetFramework="net10.0"
            )
            for dependency in dependencies:
                validate_packages.ElementTree.SubElement(
                    group, "dependency", dependency
                )
            if (
                add_ungrouped_dependency
                and contract.package_id == "ScyllaDBCSharpDriver"
            ):
                validate_packages.ElementTree.SubElement(
                    dependency_groups,
                    "dependency",
                    {
                        "id": "System.Text.Json",
                        "version": "10.0.0",
                        "exclude": "Build,Analyzers",
                    },
                )

            manifest = validate_packages.ElementTree.tostring(
                package, encoding="utf-8", xml_declaration=True
            )
            files = {
                "[Content_Types].xml": b"content types",
                "_rels/.rels": b"relationships",
                "LICENSE.md": b"license",
                f"{contract.package_id}.nuspec": manifest,
                f"lib/net10.0/{contract.assembly_name}.dll": b"assembly",
                f"lib/net10.0/{contract.assembly_name}.xml": b"documentation",
                "package/services/metadata/core-properties/nuget.psmdcp": b"metadata",
            }
            files.update(extra_files.get(contract.package_id, {}))

            archive_path = (
                package_directory
                / f"{contract.package_id}.{validate_packages.CANDIDATE_VERSION}.nupkg"
            )
            with zipfile.ZipFile(archive_path, "w") as archive:
                for path, contents in files.items():
                    archive.writestr(path, contents)
                if duplicate_file and duplicate_file[0] == contract.package_id:
                    archive.writestr(duplicate_file[1], b"duplicate")


if __name__ == "__main__":
    unittest.main()
