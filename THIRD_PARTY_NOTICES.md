# Third-party material and license scope

Goblin's original source code, documentation, and build and deployment files are
licensed under the [Apache License, Version 2.0](LICENSE). Third-party material
retains its own copyright, license, and notices. The project license does not
replace those terms or grant rights to hosted services.

## Checked-in material

| Material | Source and license |
| --- | --- |
| Imported agent skills identified by `source: "emilkowalski/skills"` in `skills-lock.json`, under `.agents/skills/`, including their recipes and reference files | [Emil Kowalski's skills, revision 2d228630ba1446365f614a108cb8252f1feda202](https://github.com/emilkowalski/skills/tree/2d228630ba1446365f614a108cb8252f1feda202), MIT. Copyright (c) 2026 Emil Kowalski. The full upstream license accompanies the skills in [.agents/skills/licenses/emilkowalski-skills/LICENSE](.agents/skills/licenses/emilkowalski-skills/LICENSE). These imported instructions and examples retain their MIT license; repository-authored skills remain under Goblin's project license. |
| Upstream schemas in `backend/schemas/codex/` and models generated from them in `backend/src/Goblin.Protocol/Generated/` | [OpenAI Codex 0.161.0](https://github.com/openai/codex/tree/rust-v0.161.0), Apache-2.0. The upstream notice is preserved in `NOTICE`. Goblin selects schema fields and generates C# models; see the protocol project's README. |
| `backend/schemas/kubernetes/kubernetes-v1.37.0-swagger.json.gz` and models generated from it | [Kubernetes v1.37.0](https://github.com/kubernetes/kubernetes/tree/v1.37.0), Apache-2.0. Goblin compresses the upstream schema and generates selected C# models. |
| `backend/schemas/kubernetes/sandbox-v1beta1.json` and models generated from it | [Kubernetes SIGs Agent Sandbox v1.0.4](https://github.com/kubernetes-sigs/agent-sandbox/tree/v1.0.4), Apache-2.0. Goblin extracts the schema from the release CRD and generates selected C# models. |
| `assets/branding/fonts/inter-18pt-*.ttf` | [Inter 4.001, revision 66647c0bb](https://github.com/rsms/inter/tree/66647c0bb), SIL Open Font License 1.1. The original copyright and full terms accompany the fonts in `assets/branding/fonts/OFL.txt`. The fonts remain under OFL-1.1. |
| `frontend/src/settings/timezone-places.ts` | Generated from `zone.tab` and `tzdata.zi` in [IANA tzdb 2026c](https://github.com/eggert/tz/tree/2026c). These timezone data are public domain; the generated file records their provenance. |
| `frontend/public/assets/providers/*.svg` | GitHub, Slack, and Microsoft Teams logos from [SVG Logos, revision 37a6b807](https://github.com/gilbarbara/logos/tree/37a6b807fd71c622efea27a9309b5d4edc792969/logos), CC0-1.0. Unmodified copies of `github-icon.svg`, `slack-icon.svg`, and `microsoft-teams.svg`; the full license is preserved in `frontend/public/assets/providers/LICENSE.txt`. Provider trademarks remain the property of their respective owners. |
| `frontend/public/assets/fonts/`, installer font assets | Copies of the Inter fonts above. The frontend and installation catalog include `OFL.txt`; the standalone installer serves the font license at `/setup/OFL.txt` and installs it as `Inter-OFL.txt` with its other licenses. |
| `website/assets/` | Website copies of Inter fonts retain `OFL.txt`; provider artwork retains `PROVIDER-LICENSE.txt`. See [the website's asset notes](website/README.md) for provenance and brand artwork exclusions. |

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

When copying or redistributing the imported agent skills or substantial portions
of their instructions and examples, include their full MIT copyright and
permission notice. The license permits personal and commercial use, modification,
and redistribution; it does not require a visible website credit. Using the
skills as development guidance does not by itself relicense Goblin's original
code. Copied or adapted upstream examples retain their applicable MIT notice.
The imported skills are development material and are not shipped in the
application or website images.

The npm and Cargo lockfiles and .NET package references identify Goblin's software
dependencies. Their own licenses apply. MIT, BSD, PostgreSQL, Zlib, Unicode, and
other notices must be retained where required; an Apache alternative does not
remove an additional license required by a dependency.

The official [Wolverine 6.39.1](https://github.com/JasperFx/wolverine) NuGet
packages use the MIT license. Its copyright notice and full terms are preserved
in [backend/licenses/wolverine/LICENSE](backend/licenses/wolverine/LICENSE),
included with the published application at `licenses/wolverine/LICENSE` and
in the application image at `/app/licenses/wolverine/LICENSE`.

The ChatGPT integration uses the official [MCP C# SDK 1.4.1](https://github.com/modelcontextprotocol/csharp-sdk),
Apache-2.0, and [Microsoft IdentityModel 8.19.1](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet),
MIT. Their upstream licenses are preserved in
[backend/licenses/modelcontextprotocol/LICENSE](backend/licenses/modelcontextprotocol/LICENSE)
and [backend/licenses/identitymodel/LICENSE](backend/licenses/identitymodel/LICENSE),
and accompany the published application under the corresponding `licenses/` paths.

The embedded OAuth server uses [OpenIddict 7.7.2](https://github.com/openiddict/openiddict-core/tree/7.7.2),
Apache-2.0. Its full license is preserved in
[backend/licenses/openiddict/LICENSE](backend/licenses/openiddict/LICENSE) and
included in the published application at `licenses/openiddict/LICENSE`.

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
The separate website image includes them under `/usr/share/doc/goblin-website/`.
These copies describe Goblin's license and do not replace dependencies' terms.

OpenAI, Slack, GitHub, Azure, container registries, and other hosted services have
separate account, API, and distribution terms. Apache-2.0 grants no service access
or commercial-distribution authorization from those providers.

## Emil Kowalski skills: MIT license

The notice below also covers any copied or adapted examples from these skills
that accompany a Goblin distribution.

```text
MIT License

Copyright (c) 2026 Emil Kowalski

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
