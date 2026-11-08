# Security Policy

## Supported versions

Inkwright is currently a pre-1.0 project. Security fixes are made for the latest published minor
release only.

| Version | Supported |
| --- | --- |
| Latest 0.1.x patch | Yes |
| Superseded releases | No |

Users should update to the latest patch release before reporting a problem that may already have
been fixed.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull request, test file,
or sample document.

Use GitHub's private vulnerability reporting form:

https://github.com/WrightDocumentSuite/inkwright/security/advisories/new

Repository maintainers keep private vulnerability reporting enabled and verify this link after a
repository transfer or security-settings change.

Include, where possible:

- the affected Inkwright package and version;
- the target framework, operating system, and architecture;
- the smallest reproduction that demonstrates the impact;
- whether an untrusted PDF, certificate, signature, or other input is required;
- any suggested mitigation or embargo constraints.

Remove personal data, credentials, private keys, and unrelated document contents from the report.
If the private reporting form is unavailable, open a public issue containing no vulnerability
details and ask the maintainers to enable a private reporting channel.

The project targets acknowledgement of a complete report within three business days and an initial
severity/affected-version assessment within seven business days. If investigation needs longer, the
private advisory will receive a status update and the next expected update date. The maintainers
coordinate the fix and disclosure there; please allow time for downstream packages to be updated
before publishing technical details.
