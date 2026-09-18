# Contributing

## Building and testing

See [`docs/development.md`](docs/development.md) for the full build, including the native helper
attach mode requires. `.\validate.ps1` runs the whole validation matrix (build, both target-framework
legs, x86/x64, format check) in one command and is what CI runs.

## Pull requests

All PRs are reviewed and merged by the maintainer - this is a solo-maintained project, so every
change, including the maintainer's own, goes through review before landing on `main`. That means:

- Open an issue first for anything nontrivial, so the approach can be agreed on before you put time
  into it.
- Keep PRs focused - one change, one reason. A PR that mixes an unrelated cleanup with a feature is
  harder to review and more likely to be asked to split.
- Match the existing code's conventions and comment density rather than introducing a new style -
  `docs/development.md` describes the architecture and the reasoning behind the less obvious
  decisions.
- Update the relevant docs (`README.md`, `docs/development.md`, `docs/threat-model.md`,
  `skills/scry/SKILL.md`) when behavior changes - a PR that changes documented behavior without
  updating the doc that documents it will be asked to include that update.

## Bug reports

Use the bug report issue template - it requires the details needed to reproduce a problem (version,
target runtime, exact command, expected vs. actual). An issue that says only "not working" will be
closed and asked to be resubmitted with that information.

## Security issues

Do not open a public issue for a suspected vulnerability. See [`SECURITY.md`](SECURITY.md).
