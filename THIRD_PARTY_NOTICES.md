# Third-party material and license scope

Goblin's original source code, documentation, and build and deployment files are
licensed under the [Apache License, Version 2.0](LICENSE). Third-party material
retains its own copyright, license, and notices. The project license does not
replace those terms or grant rights to hosted services.

## Checked-in material

| Material | Source and license |
| --- | --- |
| Upstream schemas in `backend/schemas/codex/` and models generated from them in `backend/src/Goblin.Protocol/Generated/` | [OpenAI Codex 0.161.0](https://github.com/openai/codex/tree/rust-v0.161.0), Apache-2.0. The upstream notice is preserved in `NOTICE`. Goblin selects schema fields and generates C# models; see the protocol project's README. |
| `backend/schemas/kubernetes/kubernetes-v1.37.0-swagger.json.gz` and models generated from it | [Kubernetes v1.37.0](https://github.com/kubernetes/kubernetes/tree/v1.37.0), Apache-2.0. Goblin compresses the upstream schema and generates selected C# models. |
| `backend/schemas/kubernetes/sandbox-v1beta1.json` and models generated from it | [Kubernetes SIGs Agent Sandbox v1.0.4](https://github.com/kubernetes-sigs/agent-sandbox/tree/v1.0.4), Apache-2.0. Goblin extracts the schema from the release CRD and generates selected C# models. |
| `assets/branding/fonts/inter-18pt-*.ttf` | [Inter 4.001, revision 66647c0bb](https://github.com/rsms/inter/tree/66647c0bb), SIL Open Font License 1.1. The original copyright and full terms accompany the fonts in `assets/branding/fonts/OFL.txt`. The fonts remain under OFL-1.1. |
| `frontend/src/settings/timezone-places.ts` | Generated from `zone.tab` and `tzdata.zi` in [IANA tzdb 2026c](https://github.com/eggert/tz/tree/2026c). These timezone data are public domain; the generated file records their provenance. |
| `frontend/public/assets/providers/*.svg` | GitHub, Slack, and Microsoft Teams logos from [SVG Logos, revision 37a6b807](https://github.com/gilbarbara/logos/tree/37a6b807fd71c622efea27a9309b5d4edc792969/logos), CC0-1.0. Unmodified copies of `github-icon.svg`, `slack-icon.svg`, and `microsoft-teams.svg`; the full license is preserved in `frontend/public/assets/providers/LICENSE.txt`. Provider trademarks remain the property of their respective owners. |

## Brand artwork

The supplied icons, logos, image exports, and editable design files in
`assets/branding/`, and copies or derivatives of that artwork elsewhere in the
repository or embedded in builds, are outside Goblin's Apache license grant.
This includes `frontend/public/assets/branding/` and the installer icon.
No additional copyright permission for that artwork is granted here. The Inter
font files have the separate OFL grant described above. Documentation and code
in those directories remain under Apache-2.0 unless otherwise identified.

Apache-2.0 does not grant permission to use Goblin's or upstream projects'
trademarks, except for the descriptive uses specified in section 6 of the license.

## Dependencies and distributions

The npm and Cargo lockfiles and .NET package references identify Goblin's software
dependencies. Their own licenses apply. MIT, BSD, PostgreSQL, Zlib, Unicode, and
other notices must be retained where required; an Apache alternative does not
remove an additional license required by a dependency.

The application image also includes independently licensed software such as
Codex and its native helpers, GitHub CLI, Git, and the .NET runtime. Deployed
services and operating-system packages retain their own licenses. In particular,
Git carries GPL terms, and some Codex native helpers and voice libraries carry
LGPL terms. Their separate use does not relicense Goblin's original code.
Redistributors must preserve the applicable notices and satisfy corresponding
source and replacement/relinking requirements for the binaries they distribute.

This file identifies the project license boundary and selected third-party
material; it is not a complete notice bundle or source offer for every dependency
in a built image or native executable. Preserve upstream licensing files and
review the exact distribution when preparing binary releases.

The native installer archive includes `LICENSE`, `NOTICE`, and this file, and
installation writes copies under `/opt/goblin/share/licenses/goblinctl/`.
The application image includes these documents under `/usr/share/doc/goblin/`.
These copies describe Goblin's license and do not replace dependencies' terms.

OpenAI, Slack, GitHub, Azure, container registries, and other hosted services have
separate account, API, and distribution terms. Apache-2.0 grants no service access
or commercial-distribution authorization from those providers.
