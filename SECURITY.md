# Security Policy

## Reporting a vulnerability

Please report vulnerabilities through
[GitHub private vulnerability reporting](../../security/advisories/new) —
do not open a public issue for security problems.

You should receive a response within a week. Please include reproduction steps
and the affected version/commit.

## Supported versions

This is a template repository: fixes land on `master` only. Projects generated
from the template own their security posture from the moment of generation —
re-run `npm audit` and `dotnet list package --vulnerable` regularly (the
generated CI does this via Dependabot and dependency review).
