import importlib.util
from pathlib import Path
from unittest import TestCase
from unittest.mock import patch


MODULE_PATH = Path(__file__).resolve().parents[1] / "remove-release-review.py"
SPEC = importlib.util.spec_from_file_location("remove_release_review", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class RemoveReleaseReviewTests(TestCase):
    def setUp(self):
        self.environment = {
            "deployment_branch_policy": {
                "protected_branches": False,
                "custom_branch_policies": True,
            },
            "protection_rules": [
                {"type": "required_reviewers"},
                {"type": "branch_policy"},
            ],
        }
        self.policies = {
            "branch_policies": [
                {"name": "master"},
                {"name": "branch-3.22"},
            ],
        }
        self.secrets = {
            "secrets": [
                {"name": "RELEASE_BOT_TOKEN"},
                {"name": "RELEASE_GPG_PRIVATE_KEY"},
            ],
        }

    def test_dry_run_does_not_update_environment(self):
        with patch.object(MODULE, "gh_api", side_effect=[self.environment, self.policies, self.secrets]) as api:
            MODULE.remove_review("scylladb/csharp-driver", apply=False)
        self.assertEqual(3, api.call_count)

    def test_update_preserves_branch_policy_and_checks_secrets(self):
        updated = {
            **self.environment,
            "protection_rules": [{"type": "branch_policy"}],
        }
        responses = [
            self.environment, self.policies, self.secrets,
            updated,
            updated, self.policies, self.secrets,
        ]
        with patch.object(MODULE, "gh_api", side_effect=responses) as api:
            MODULE.remove_review("scylladb/csharp-driver", apply=True)
        self.assertEqual(
            {
                "wait_timer": 0,
                "prevent_self_review": False,
                "reviewers": [],
                "deployment_branch_policy": self.environment["deployment_branch_policy"],
            },
            api.call_args_list[3].kwargs["payload"],
        )

    def test_rejects_unexpected_branch_policy_before_update(self):
        policies = {"branch_policies": [{"name": "master"}]}
        with patch.object(MODULE, "gh_api", side_effect=[self.environment, policies, self.secrets]) as api:
            with self.assertRaises(ValueError):
                MODULE.remove_review("scylladb/csharp-driver", apply=True)
        self.assertEqual(3, api.call_count)
