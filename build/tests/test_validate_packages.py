import importlib.util
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

            with mock.patch.object(validate_packages, "DOTNET", "/selected/dotnet"):
                with mock.patch.object(
                    validate_packages, "run", return_value=completed
                ) as run:
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._assemblies("candidate"),
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

            with mock.patch.object(
                validate_packages, "run", side_effect=[installed, unresolved]
            ):
                with self.assertRaisesRegex(
                    validate_packages.ValidationError,
                    "could not resolve all references",
                ):
                    validate_packages.run_api_compat(
                        work_directory,
                        self._assemblies("baseline"),
                        self._assemblies("candidate"),
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
