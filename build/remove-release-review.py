#!/usr/bin/env python3
"""Remove the release environment's manual review gate, preserving branch access."""

import argparse
import json
import subprocess


ENVIRONMENT = "release"
EXPECTED_BRANCHES = {"master", "branch-3.22"}
EXPECTED_SECRETS = {"RELEASE_BOT_TOKEN", "RELEASE_GPG_PRIVATE_KEY"}


def gh_api(path, *, payload=None):
    command = ["gh", "api"]
    if payload is not None:
        command.extend(["-X", "PUT", "--input", "-"])
    command.append(path)
    result = subprocess.run(
        command,
        input=json.dumps(payload) if payload is not None else None,
        text=True,
        capture_output=True,
        check=True,
    )
    return json.loads(result.stdout)


def inspect(repository):
    path = f"repos/{repository}/environments/{ENVIRONMENT}"
    environment = gh_api(path)
    policies = gh_api(f"{path}/deployment-branch-policies")
    secrets = gh_api(f"{path}/secrets")

    branch_policy = environment["deployment_branch_policy"]
    if branch_policy != {"protected_branches": False, "custom_branch_policies": True}:
        raise ValueError("release environment must use custom deployment branch policies")
    branches = {entry["name"] for entry in policies["branch_policies"]}
    if branches != EXPECTED_BRANCHES:
        raise ValueError(f"Unexpected release deployment branches: {sorted(branches)}")
    secret_names = {entry["name"] for entry in secrets["secrets"]}
    if not EXPECTED_SECRETS <= secret_names:
        raise ValueError("Release environment is missing publisher credentials")

    rules = environment["protection_rules"]
    unexpected = {rule["type"] for rule in rules} - {"branch_policy", "required_reviewers"}
    if unexpected:
        raise ValueError(f"Unexpected release protection rules: {sorted(unexpected)}")
    return path, environment, branches, secret_names


def remove_review(repository, *, apply):
    path, environment, branches, secret_names = inspect(repository)
    has_reviewers = any(rule["type"] == "required_reviewers" for rule in environment["protection_rules"])
    if not has_reviewers:
        print("Release environment already has no required reviewers")
        return
    if not apply:
        print("Would remove required reviewers from the release environment; use --apply to update GitHub")
        return

    gh_api(
        path,
        payload={
            "wait_timer": 0,
            "prevent_self_review": False,
            "reviewers": [],
            "deployment_branch_policy": environment["deployment_branch_policy"],
        },
    )
    _, updated, updated_branches, updated_secrets = inspect(repository)
    if any(rule["type"] == "required_reviewers" for rule in updated["protection_rules"]):
        raise RuntimeError("Required reviewers are still configured")
    if updated_branches != branches or updated_secrets != secret_names:
        raise RuntimeError("Release branches or environment secrets changed unexpectedly")
    print("Removed required reviewers; preserved release branches and environment secrets")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--apply", action="store_true")
    arguments = parser.parse_args()
    remove_review(arguments.repository, apply=arguments.apply)


if __name__ == "__main__":
    main()
