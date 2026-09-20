import { createHash } from 'node:crypto';
import { writeFileSync, mkdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
const version = '2.101.0';
const arch = { x64: 'amd64', arm64: 'arm64' }[process.arch];
const hashes = {
  amd64: '9bca2d1c16825f109907a23307628a2f0698fbf99662b73a5cf0b020293072b8',
  arm64: 'b57e8063f18862647c9d22727c32e9da1b963f8bf9db648fe123a6975695640f',
};
if (!arch) throw new Error('Unsupported GitHub CLI architecture');
const response = await fetch(`https://github.com/cli/cli/releases/download/v${version}/gh_${version}_linux_${arch}.tar.gz`);
if (!response.ok) throw new Error('GitHub CLI download failed');
const archive = Buffer.from(await response.arrayBuffer());
if (createHash('sha256').update(archive).digest('hex') !== hashes[arch]) throw new Error('GitHub CLI checksum mismatch');
writeFileSync('/tmp/github-cli.tar.gz', archive);
mkdirSync('/githubcli');
execFileSync('tar', ['-xzf', '/tmp/github-cli.tar.gz', '--strip-components=1', '-C', '/githubcli']);
