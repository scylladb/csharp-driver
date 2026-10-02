#!/usr/bin/env python3

import argparse
import base64
import hashlib
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
NUGET_ORG = "https://api.nuget.org/v3/index.json"
BASELINE_VERSION = "3.22.0.4"
CANDIDATE_VERSION = "4.0.0"
APICOMPAT_VERSION = "10.0.401"
DOTNET = os.environ.get("DOTNET_HOST_PATH", "dotnet")


class ValidationError(RuntimeError):
    pass


@dataclass(frozen=True)
class PackageContract:
    project: str
    package_id: str
    assembly_name: str
    dependencies: dict[str, str]


PACKAGE_CONTRACTS = (
    PackageContract(
        project="src/Cassandra/Cassandra.csproj",
        package_id="ScyllaDBCSharpDriver",
        assembly_name="ScyllaDB",
        dependencies={
            "K4os.Compression.LZ4": "1.3.8",
            "Microsoft.Extensions.Logging": "10.0.12",
            "Microsoft.Extensions.Logging.Abstractions": "10.0.12",
            "Newtonsoft.Json": "13.0.4",
        },
    ),
    PackageContract(
        project="src/Extensions/Cassandra.AppMetrics/Cassandra.AppMetrics.csproj",
        package_id="ScyllaDBCSharpDriver.AppMetrics",
        assembly_name="Cassandra.AppMetrics",
        dependencies={
            "ScyllaDBCSharpDriver": CANDIDATE_VERSION,
            "App.Metrics.Abstractions": "3.2.0",
            "App.Metrics.Concurrency": "4.3.0",
        },
    ),
    PackageContract(
        project="src/Extensions/Cassandra.OpenTelemetry/Cassandra.OpenTelemetry.csproj",
        package_id="ScyllaDBCSharpDriver.OpenTelemetry",
        assembly_name="Cassandra.OpenTelemetry",
        dependencies={
            "ScyllaDBCSharpDriver": CANDIDATE_VERSION,
            "OpenTelemetry.Api": "1.19.1",
        },
    ),
)

DRY_RUN_PACKAGE_IDS = {
    "ScyllaDBCSharpDriver": "ScyllaDBCSharpDriver.DRYRUN",
    "ScyllaDBCSharpDriver.AppMetrics": "ScyllaDBCSharpDriver.DRYRUN.AppMetrics",
    "ScyllaDBCSharpDriver.OpenTelemetry": (
        "ScyllaDBCSharpDriver.DRYRUN.OpenTelemetry"
    ),
}


def candidate_contracts(dry_run: bool = False) -> tuple[PackageContract, ...]:
    if not dry_run:
        return PACKAGE_CONTRACTS

    return tuple(
        PackageContract(
            project=contract.project,
            package_id=DRY_RUN_PACKAGE_IDS[contract.package_id],
            assembly_name=contract.assembly_name,
            dependencies={
                DRY_RUN_PACKAGE_IDS.get(dependency_id, dependency_id): version
                for dependency_id, version in contract.dependencies.items()
            },
        )
        for contract in PACKAGE_CONTRACTS
    )


def log(message: str) -> None:
    print(message, flush=True)


def run(
    arguments: list[str],
    *,
    cwd: Path = REPOSITORY_ROOT,
    capture_output: bool = False,
    merge_stderr: bool = False,
    check: bool = True,
    environment: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    log(f"+ {shlex.join(str(argument) for argument in arguments)}")
    process_environment = os.environ.copy()
    if environment:
        process_environment.update(environment)
    result = subprocess.run(
        [str(argument) for argument in arguments],
        cwd=cwd,
        check=False,
        env=process_environment,
        stdout=subprocess.PIPE if capture_output else None,
        stderr=subprocess.STDOUT if capture_output and merge_stderr else None,
        text=True,
    )
    if capture_output and result.stdout:
        print(result.stdout, end="" if result.stdout.endswith("\n") else "\n")
    if check and result.returncode != 0:
        raise ValidationError(
            f"Command failed with exit code {result.returncode}: "
            f"{shlex.join(str(argument) for argument in arguments)}"
        )
    return result


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValidationError(message)


def ensure_signing_key() -> None:
    signing_key = REPOSITORY_ROOT / "build/scylladb.snk"
    if not signing_key.exists():
        shutil.copy2(REPOSITORY_ROOT / "build/scylladb-dev.snk", signing_key)


def validate_sdk() -> None:
    result = run([DOTNET, "--version"], capture_output=True)
    require(
        result.stdout.strip() == APICOMPAT_VERSION,
        f"Expected .NET SDK {APICOMPAT_VERSION}, got {result.stdout.strip()!r}",
    )


def restore_and_audit() -> None:
    log("\nRestoring and auditing the complete project and example graph")
    for solution in ("src/Cassandra.sln", "examples/examples.sln"):
        run(
            [
                DOTNET,
                "restore",
                solution,
                "--force",
                "--no-http-cache",
                "-p:NuGetAudit=true",
                "-p:NuGetAuditMode=all",
                "-p:NuGetAuditLevel=low",
                "-p:TreatWarningsAsErrors=true",
                "-p:WarningsNotAsErrors=",
            ]
        )

    audit_result = run(
        [
            DOTNET,
            "package",
            "list",
            "--project",
            "src/Cassandra.sln",
            "--vulnerable",
            "--include-transitive",
            "--format",
            "json",
            "--output-version",
            "1",
            "--no-restore",
            "--source",
            NUGET_ORG,
        ],
        capture_output=True,
    )
    try:
        audit = json.loads(audit_result.stdout)
    except json.JSONDecodeError as exception:
        raise ValidationError("dotnet package list did not emit valid JSON") from exception

    vulnerable_nodes: list[str] = []

    def find_vulnerabilities(value: object, location: str) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                child_location = f"{location}.{key}" if location else key
                if key == "vulnerabilities" and child:
                    vulnerable_nodes.append(child_location)
                find_vulnerabilities(child, child_location)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                find_vulnerabilities(child, f"{location}[{index}]")

    find_vulnerabilities(audit, "")
    require(
        not vulnerable_nodes,
        "Vulnerable direct or transitive packages were reported at: "
        + ", ".join(vulnerable_nodes),
    )


def pack_candidates(package_directory: Path) -> None:
    log("\nPacking release candidates")
    package_directory.mkdir(parents=True)
    for contract in PACKAGE_CONTRACTS:
        run(
            [
                DOTNET,
                "pack",
                contract.project,
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                str(package_directory),
            ]
        )


def child(element: ElementTree.Element, name: str) -> ElementTree.Element:
    result = element.find(f"{{*}}{name}")
    if result is None:
        raise ValidationError(f"Missing <{name}> in package manifest")
    return result


def child_text(element: ElementTree.Element, name: str) -> str:
    result = child(element, name)
    return (result.text or "").strip()


def safely_extract(package: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    destination_root = destination.resolve()
    with zipfile.ZipFile(package) as archive:
        for member in archive.infolist():
            target = (destination / member.filename).resolve()
            require(
                target == destination_root or target.is_relative_to(destination_root),
                f"Unsafe archive entry {member.filename!r} in {package.name}",
            )
        archive.extractall(destination)


def inspect_candidates(
    package_directory: Path,
    extraction_root: Path,
    contracts: tuple[PackageContract, ...] = PACKAGE_CONTRACTS,
) -> dict[str, Path]:
    log("\nInspecting package assets and dependency groups")
    packages = sorted(package_directory.glob("*.nupkg"))
    require(
        len(packages) == len(contracts),
        f"Expected {len(contracts)} packages, found {len(packages)}",
    )

    contracts_by_id = {contract.package_id: contract for contract in contracts}
    assemblies: dict[str, Path] = {}

    for package in packages:
        with zipfile.ZipFile(package) as archive:
            archive_files = [
                name for name in archive.namelist() if name and not name.endswith("/")
            ]
            duplicate_files = sorted(
                name for name, count in Counter(archive_files).items() if count > 1
            )
            require(
                not duplicate_files,
                f"Duplicate package entries in {package.name}: {duplicate_files}",
            )
            files = set(archive_files)
            manifests = [name for name in files if name.endswith(".nuspec")]
            require(
                len(manifests) == 1,
                f"Expected one nuspec in {package.name}, found {len(manifests)}",
            )
            manifest = ElementTree.fromstring(archive.read(manifests[0]))
            metadata = child(manifest, "metadata")
            package_id = child_text(metadata, "id")
            require(
                package_id in contracts_by_id,
                f"Unexpected package ID {package_id!r} in {package.name}",
            )
            contract = contracts_by_id[package_id]
            require(
                package_id not in assemblies,
                f"Found more than one package for {package_id}",
            )
            require(
                child_text(metadata, "version") == CANDIDATE_VERSION,
                f"{package_id} must pack normalized version {CANDIDATE_VERSION}",
            )

            expected_files = {
                "[Content_Types].xml",
                "_rels/.rels",
                "LICENSE.md",
                f"{contract.package_id}.nuspec",
                f"lib/net10.0/{contract.assembly_name}.dll",
                f"lib/net10.0/{contract.assembly_name}.xml",
                "package/services/metadata/core-properties/nuget.psmdcp",
            }
            unexpected_files = sorted(files - expected_files)
            missing_files = sorted(expected_files - files)
            require(
                not unexpected_files and not missing_files,
                f"Unexpected package contents in {package_id}: "
                f"unexpected {unexpected_files}, missing {missing_files}",
            )

            dependencies = child(metadata, "dependencies")
            groups = dependencies.findall("{*}group")
            require(
                len(groups) == 1 and len(list(dependencies)) == 1,
                f"{package_id} must contain exactly one grouped dependency set",
            )
            group = groups[0]
            require(
                group.attrib == {"targetFramework": "net10.0"},
                f"{package_id} dependency group must target net10.0",
            )
            dependency_elements = group.findall("{*}dependency")
            require(
                len(dependency_elements) == len(list(group)),
                f"{package_id} dependency group contains unexpected elements",
            )
            actual_dependencies = Counter(
                tuple(sorted(dependency.attrib.items()))
                for dependency in dependency_elements
            )
            expected_dependencies = Counter(
                tuple(
                    sorted(
                        {
                            "id": dependency_id,
                            "version": version,
                            "exclude": "Build,Analyzers",
                        }.items()
                    )
                )
                for dependency_id, version in contract.dependencies.items()
            )
            require(
                actual_dependencies == expected_dependencies,
                f"Unexpected dependencies in {package_id}: "
                f"expected {expected_dependencies}, got {actual_dependencies}",
            )

        extraction_directory = extraction_root / package_id
        safely_extract(package, extraction_directory)
        assembly = extraction_directory / f"lib/net10.0/{contract.assembly_name}.dll"
        require(assembly.is_file(), f"Missing candidate assembly {assembly}")
        assemblies[package_id] = assembly
        log(f"Validated {package.name}")

    require(
        set(assemblies) == set(contracts_by_id),
        "One or more expected packages were not produced",
    )
    return assemblies


def download(url: str, destination: Path) -> None:
    log(f"Downloading {url}")
    request = urllib.request.Request(
        url, headers={"User-Agent": "scylladb-csharp-driver-package-validation"}
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        require(response.status == 200, f"Download returned HTTP {response.status}: {url}")
        with destination.open("wb") as output:
            shutil.copyfileobj(response, output)


def download_baselines(baseline_root: Path) -> dict[str, Path]:
    log("\nDownloading published 3.22.0.4 baselines")
    baseline_root.mkdir(parents=True)
    assemblies: dict[str, Path] = {}
    for contract in PACKAGE_CONTRACTS:
        package_name = contract.package_id.lower()
        package = baseline_root / f"{package_name}.{BASELINE_VERSION}.nupkg"
        url = (
            "https://api.nuget.org/v3-flatcontainer/"
            f"{package_name}/{BASELINE_VERSION}/{package_name}.{BASELINE_VERSION}.nupkg"
        )
        download(url, package)
        extraction_directory = baseline_root / contract.package_id
        safely_extract(package, extraction_directory)
        assembly = (
            extraction_directory
            / f"lib/netstandard2.0/{contract.assembly_name}.dll"
        )
        require(assembly.is_file(), f"Missing baseline assembly {assembly}")
        assemblies[contract.package_id] = assembly
    return assemblies


def write_baseline_restore_project(project: Path) -> None:
    package_references = "\n".join(
        f'    <PackageReference Include="{contract.package_id}" '
        f'Version="{BASELINE_VERSION}" />'
        for contract in PACKAGE_CONTRACTS
    )
    project.write_text(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        "  <PropertyGroup>\n"
        "    <TargetFramework>netstandard2.0</TargetFramework>\n"
        "  </PropertyGroup>\n"
        "  <ItemGroup>\n"
        f"{package_references}\n"
        "  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )


def compile_assets(assets_files: list[Path], target_framework: str) -> list[Path]:
    result: dict[Path, None] = {}
    for assets_file in assets_files:
        assets = json.loads(assets_file.read_text(encoding="utf-8"))
        targets = assets.get("targets", {})
        target_key = next(
            (
                key
                for key in targets
                if key.split("/", 1)[0].lower() == target_framework.lower()
            ),
            None,
        )
        require(
            target_key is not None,
            f"{assets_file} does not contain target {target_framework}",
        )
        packages_root = Path(assets["project"]["restore"]["packagesPath"])
        for library_key, target in targets[target_key].items():
            library = assets["libraries"].get(library_key, {})
            if library.get("type") != "package":
                continue
            library_root = packages_root / library["path"]
            for relative_path in target.get("compile", {}):
                if not relative_path.lower().endswith(".dll"):
                    continue
                assembly = library_root / relative_path
                require(assembly.is_file(), f"Missing restored reference {assembly}")
                result[assembly] = None
    return list(result)


def stage_references(
    destination: Path,
    package_references: list[Path],
    primary_assemblies: list[Path],
    framework_references: Path,
) -> None:
    destination.mkdir(parents=True)

    def copy_reference(reference: Path, *, overwrite: bool = False) -> None:
        target = destination / reference.name
        if target.exists() and not overwrite:
            require(
                target.read_bytes() == reference.read_bytes(),
                f"Conflicting reference assemblies named {reference.name}",
            )
            return
        shutil.copy2(reference, target)

    for reference in package_references:
        copy_reference(reference)
    for assembly in primary_assemblies:
        copy_reference(assembly)
    for reference in sorted(framework_references.glob("*.dll")):
        copy_reference(reference, overwrite=True)


def net10_reference_pack() -> Path:
    # Ask the selected SDK which targeting pack it resolved for the actual
    # shipping project. An installation can contain packs from several SDKs,
    # so selecting the greatest directory would not necessarily match 10.0.401.
    result = run(
        [
            DOTNET,
            "msbuild",
            PACKAGE_CONTRACTS[0].project,
            "-nologo",
            "-target:ResolveFrameworkReferences",
            "-getItem:ResolvedTargetingPack",
        ],
        capture_output=True,
    )
    try:
        query = json.loads(result.stdout)
    except json.JSONDecodeError as exception:
        raise ValidationError("MSBuild did not emit valid targeting-pack JSON") from exception

    targeting_packs = [
        item
        for item in query.get("Items", {}).get("ResolvedTargetingPack", [])
        if item.get("NuGetPackageId") == "Microsoft.NETCore.App.Ref"
        and item.get("TargetFramework") == "net10.0"
    ]
    require(
        len(targeting_packs) == 1,
        "Expected MSBuild to resolve one Microsoft.NETCore.App.Ref pack for net10.0, "
        f"found {len(targeting_packs)}",
    )

    package_directory = targeting_packs[0].get("PackageDirectory")
    require(
        bool(package_directory),
        "The resolved Microsoft.NETCore.App.Ref pack has no package directory",
    )
    reference_pack = Path(package_directory) / "ref/net10.0"
    require(
        reference_pack.is_dir(),
        f"Could not locate the resolved .NET 10 reference pack at {reference_pack}",
    )
    return reference_pack


def prepare_reference_sets(
    work_directory: Path,
    baseline_assemblies: dict[str, Path],
    candidate_assemblies: dict[str, Path],
) -> tuple[Path, Path]:
    log("\nPreparing complete ApiCompat reference sets")
    baseline_project_directory = work_directory / "baseline-restore"
    baseline_project_directory.mkdir()
    baseline_project = baseline_project_directory / "Baseline.csproj"
    write_baseline_restore_project(baseline_project)
    baseline_packages = work_directory / "baseline-packages"
    run(
        [
            DOTNET,
            "restore",
            str(baseline_project),
            "--force",
            "--no-http-cache",
            "--packages",
            str(baseline_packages),
            "--source",
            NUGET_ORG,
        ]
    )

    baseline_assets = baseline_project_directory / "obj/project.assets.json"
    baseline_package_references = compile_assets(
        [baseline_assets], "netstandard2.0"
    )
    netstandard_references = (
        baseline_packages
        / "netstandard.library/2.0.3/build/netstandard2.0/ref"
    )
    require(
        netstandard_references.is_dir(),
        f"Could not locate .NET Standard reference assemblies at {netstandard_references}",
    )

    candidate_assets = [
        REPOSITORY_ROOT / Path(contract.project).parent / "obj/project.assets.json"
        for contract in PACKAGE_CONTRACTS
    ]
    candidate_package_references = compile_assets(candidate_assets, "net10.0")

    left_references = work_directory / "apicompat-left-references"
    right_references = work_directory / "apicompat-right-references"
    stage_references(
        left_references,
        baseline_package_references,
        list(baseline_assemblies.values()),
        netstandard_references,
    )
    stage_references(
        right_references,
        candidate_package_references,
        list(candidate_assemblies.values()),
        net10_reference_pack(),
    )
    return left_references, right_references


def run_api_compat(
    work_directory: Path,
    baseline_assemblies: dict[str, Path],
    candidate_assemblies: dict[str, Path],
    left_references: Path,
    right_references: Path,
) -> None:
    log("\nComparing public APIs with 3.22.0.4")
    tool_directory = work_directory / "tools"
    run(
        [
            DOTNET,
            "tool",
            "install",
            "Microsoft.DotNet.ApiCompat.Tool",
            "--tool-path",
            str(tool_directory),
            "--version",
            APICOMPAT_VERSION,
            "--no-cache",
        ]
    )
    tool_assemblies = list(
        tool_directory.glob(".store/**/Microsoft.DotNet.ApiCompat.Tool.dll")
    )
    require(
        len(tool_assemblies) == 1,
        f"Expected one ApiCompat tool assembly, found {len(tool_assemblies)}",
    )
    tool_assembly = tool_assemblies[0]

    for contract in PACKAGE_CONTRACTS:
        result = run(
            [
                DOTNET,
                "exec",
                str(tool_assembly),
                "--left-assembly",
                str(baseline_assemblies[contract.package_id]),
                "--right-assembly",
                str(candidate_assemblies[contract.package_id]),
                "--left-assembly-references",
                str(left_references),
                "--right-assembly-references",
                str(right_references),
                "--noWarn",
                "CP0003",
                "--verbosity",
                "High",
            ],
            capture_output=True,
            merge_stderr=True,
            check=False,
            # ApiCompat reports unresolved references as localized warnings and
            # still returns zero. Keep the diagnostic below stable on every OS.
            environment={"DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1"},
        )
        require(
            "Could not resolve reference" not in result.stdout,
            f"ApiCompat could not resolve all references for {contract.package_id}",
        )
        require(
            result.returncode == 0,
            f"ApiCompat found a public API break in {contract.package_id}",
        )


def write_consumer_project(
    project: Path, target_framework: str, contracts: tuple[PackageContract, ...]
) -> None:
    package_references = "\n".join(
        f'    <PackageReference Include="{contract.package_id}" '
        f'Version="{CANDIDATE_VERSION}" />'
        for contract in contracts
    )
    project.write_text(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        "  <PropertyGroup>\n"
        "    <OutputType>Exe</OutputType>\n"
        f"    <TargetFramework>{target_framework}</TargetFramework>\n"
        "  </PropertyGroup>\n"
        "  <ItemGroup>\n"
        f"{package_references}\n"
        "  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )


def assert_local_packages_were_restored(
    assets_file: Path,
    package_directory: Path,
    contracts: tuple[PackageContract, ...] = PACKAGE_CONTRACTS,
) -> None:
    assets = json.loads(assets_file.read_text(encoding="utf-8"))
    libraries = assets.get("libraries", {})
    for contract in contracts:
        library_key = f"{contract.package_id}/{CANDIDATE_VERSION}"
        require(
            library_key in libraries,
            f"The net10.0 consumer did not restore {library_key}",
        )
        packages = list(
            package_directory.glob(f"{contract.package_id}.{CANDIDATE_VERSION}.nupkg")
        )
        require(
            len(packages) == 1,
            f"Could not identify the local package for {contract.package_id}",
        )
        expected_hash = base64.b64encode(
            hashlib.sha512(packages[0].read_bytes()).digest()
        ).decode("ascii")
        require(
            libraries[library_key].get("sha512") == expected_hash,
            f"The net10.0 consumer did not use the local {contract.package_id} package",
        )


def write_consumer_nuget_config(
    config: Path,
    package_directory: Path,
    contracts: tuple[PackageContract, ...] = PACKAGE_CONTRACTS,
) -> None:
    configuration = ElementTree.Element("configuration")
    sources = ElementTree.SubElement(configuration, "packageSources")
    ElementTree.SubElement(sources, "clear")
    ElementTree.SubElement(
        sources, "add", key="local-candidates", value=str(package_directory)
    )
    ElementTree.SubElement(sources, "add", key="nuget.org", value=NUGET_ORG)

    mappings = ElementTree.SubElement(configuration, "packageSourceMapping")
    local_mapping = ElementTree.SubElement(
        mappings, "packageSource", key="local-candidates"
    )
    for contract in contracts:
        ElementTree.SubElement(local_mapping, "package", pattern=contract.package_id)
    nuget_mapping = ElementTree.SubElement(
        mappings, "packageSource", key="nuget.org"
    )
    ElementTree.SubElement(nuget_mapping, "package", pattern="*")
    ElementTree.ElementTree(configuration).write(
        config, encoding="utf-8", xml_declaration=True
    )


def validate_incompatible_consumers(
    work_directory: Path,
    nuget_config: Path,
    contracts: tuple[PackageContract, ...] = PACKAGE_CONTRACTS,
) -> None:
    for contract in contracts:
        net9_directory = work_directory / f"consumer-net9-{contract.package_id}"
        net9_directory.mkdir()
        net9_project = net9_directory / "Consumer.csproj"
        write_consumer_project(net9_project, "net9.0", (contract,))
        restore = run(
            [
                DOTNET,
                "restore",
                str(net9_project),
                "--force",
                "--no-http-cache",
                "--configfile",
                str(nuget_config),
                "--packages",
                str(net9_directory / "packages"),
            ],
            capture_output=True,
            merge_stderr=True,
            check=False,
        )
        require(
            restore.returncode != 0,
            f"A net9.0 consumer unexpectedly restored {contract.package_id}",
        )
        expected_failure_markers = (
            "NU1202",
            contract.package_id,
            "net9.0",
            "net10.0",
        )
        missing_markers = [
            marker
            for marker in expected_failure_markers
            if marker not in restore.stdout
        ]
        require(
            not missing_markers,
            f"The net9.0 restore of {contract.package_id} failed for an unexpected "
            "reason; missing output markers: " + ", ".join(missing_markers),
        )


def validate_consumers(
    work_directory: Path,
    package_directory: Path,
    contracts: tuple[PackageContract, ...] = PACKAGE_CONTRACTS,
) -> None:
    log("\nValidating local-feed consumers")
    nuget_config = work_directory / "NuGet.Config"
    write_consumer_nuget_config(nuget_config, package_directory, contracts)
    net10_directory = work_directory / "consumer-net10"
    net10_directory.mkdir()
    net10_project = net10_directory / "Consumer.csproj"
    write_consumer_project(net10_project, "net10.0", contracts)
    (net10_directory / "Program.cs").write_text(
        "using System;\n"
        "using System.Reflection;\n"
        "using System.Runtime.Versioning;\n"
        "using Cassandra;\n"
        "using Cassandra.AppMetrics;\n"
        "using Cassandra.OpenTelemetry;\n"
        "using K4os.Compression.LZ4;\n"
        "\n"
        "static void AssertAssembly(Type markerType, string expectedName)\n"
        "{\n"
        "    Assembly assembly = markerType.Assembly;\n"
        "    if (assembly.GetName().Name != expectedName ||\n"
        "        assembly.GetName().Version?.ToString() != \"4.99.0.0\" ||\n"
        "        assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version "
        "!= \"4.0.0.0\" ||\n"
        "        assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName "
        "!= \".NETCoreApp,Version=v10.0\")\n"
        "    {\n"
        "        throw new InvalidOperationException($\"Unexpected assembly metadata: "
        "{assembly.FullName}\");\n"
        "    }\n"
        "}\n"
        "\n"
        "AssertAssembly(typeof(Cluster), \"ScyllaDB\");\n"
        "AssertAssembly(typeof(DriverAppMetricsOptions), \"Cassandra.AppMetrics\");\n"
        "AssertAssembly(typeof(CassandraInstrumentationOptions), "
        "\"Cassandra.OpenTelemetry\");\n"
        "if (LZ4Codec.MaximumOutputSize(1) <= 0)\n"
        "{\n"
        "    throw new InvalidOperationException(\"K4os LZ4 is unavailable\");\n"
        "}\n",
        encoding="utf-8",
    )
    run(
        [
            DOTNET,
            "restore",
            str(net10_project),
            "--force",
            "--no-http-cache",
            "--configfile",
            str(nuget_config),
            "--packages",
            str(net10_directory / "packages"),
            "-p:NuGetAudit=true",
            "-p:NuGetAuditMode=all",
            "-p:NuGetAuditLevel=low",
            "-p:TreatWarningsAsErrors=true",
        ]
    )
    assert_local_packages_were_restored(
        net10_directory / "obj/project.assets.json", package_directory, contracts
    )
    run(
        [
            DOTNET,
            "build",
            str(net10_project),
            "--configuration",
            "Release",
            "--no-restore",
        ]
    )
    run(
        [
            DOTNET,
            "run",
            "--project",
            str(net10_project),
            "--configuration",
            "Release",
            "--no-build",
            "--no-restore",
        ]
    )

    validate_incompatible_consumers(work_directory, nuget_config, contracts)


def parse_arguments(arguments: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate the driver's NuGet release packages"
    )
    parser.add_argument(
        "--package-directory",
        type=Path,
        help=(
            "validate already-packed .nupkg files in this directory instead of "
            "packing the projects"
        ),
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="validate packages published under the ScyllaDBCSharpDriver.DRYRUN IDs",
    )
    parsed = parser.parse_args(arguments)
    if parsed.dry_run and parsed.package_directory is None:
        parser.error("--dry-run requires --package-directory")
    return parsed


def main(arguments: list[str] | None = None) -> None:
    options = parse_arguments(arguments)
    contracts = candidate_contracts(options.dry_run)
    staged_package_directory = None
    if options.package_directory is not None:
        staged_package_directory = options.package_directory.resolve()
        require(
            staged_package_directory.is_dir(),
            f"Package directory does not exist: {staged_package_directory}",
        )

    validate_sdk()
    if staged_package_directory is None:
        ensure_signing_key()
    restore_and_audit()

    with tempfile.TemporaryDirectory(
        prefix="csharp-driver-package-validation-"
    ) as temporary_directory:
        work_directory = Path(temporary_directory)
        if staged_package_directory is None:
            package_directory = work_directory / "packages"
            pack_candidates(package_directory)
        else:
            package_directory = staged_package_directory
        candidate_assemblies = inspect_candidates(
            package_directory,
            work_directory / "candidate-extracted",
            contracts,
        )
        candidate_assemblies_by_canonical_id = {
            canonical.package_id: candidate_assemblies[candidate.package_id]
            for canonical, candidate in zip(PACKAGE_CONTRACTS, contracts)
        }
        baseline_assemblies = download_baselines(work_directory / "baselines")
        left_references, right_references = prepare_reference_sets(
            work_directory,
            baseline_assemblies,
            candidate_assemblies_by_canonical_id,
        )
        run_api_compat(
            work_directory,
            baseline_assemblies,
            candidate_assemblies_by_canonical_id,
            left_references,
            right_references,
        )
        validate_consumers(work_directory, package_directory, contracts)

    log("\nPackage validation completed successfully")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValidationError, zipfile.BadZipFile) as exception:
        print(f"Package validation failed: {exception}", file=sys.stderr)
        sys.exit(1)
