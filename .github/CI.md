# Pull request CI

The local workflow `workflows/pr-build-test.yml` restores and builds the application and runs the
existing regression suites on GitHub-hosted Windows 2025. Publication is separately authorized;
these local definitions have not run on GitHub. Logs and test results are retained for seven days.
Official checkout, setup-dotnet, and upload-artifact actions are pinned to immutable commit hashes.

It uses only `pull_request`, `contents: read`, and checkout with persisted credentials disabled.
There is no publishing, deployment, signing, secret input, or privileged self-hosted runner.
Interactive desktop testing is separate; passing these jobs does not certify a user's full window,
keyboard, screen reader, or hardware.

The engine console runner runs on both net48 and net10.0. The speed-test console runner runs on
net48 with local fixtures only, without --worker or --internet. They use dotnet run because they
are executable regression suites. Isolated WPF checks are distinct from interactive UI automation.
The build SDK is .NET 10; the application and desktop runner continue targeting net48.
