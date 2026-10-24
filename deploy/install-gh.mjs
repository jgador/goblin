import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
const version = '2.101.0';
const arch = { x64: 'amd64', arm64: 'arm64' }[process.arch];
const hashes = {
  amd64: '9bca2d1c16825f109907a23307628a2f0698fbf99662b73a5cf0b020293072b8',
  arm64: 'b57e8063f18862647c9d22727c32e9da1b963f8bf9db648fe123a6975695640f',
};
if (!arch) throw new Error('Unsupported GitHub CLI architecture');
// The content hash separates versions and architectures in the BuildKit cache.
// Verify cached bytes too; a corrupt cache must never become an executable.
const cacheDirectory = '/tmp/github-cli-cache';
const archivePath = `${cacheDirectory}/${hashes[arch]}.tar.gz`;
const matches = archive => createHash('sha256').update(archive).digest('hex') === hashes[arch];
let archive = existsSync(archivePath) ? readFileSync(archivePath) : null;
if (!archive || !matches(archive)) {
  const response = await fetch(`https://github.com/cli/cli/releases/download/v${version}/gh_${version}_linux_${arch}.tar.gz`);
  if (!response.ok) throw new Error('GitHub CLI download failed');
  archive = Buffer.from(await response.arrayBuffer());
  if (!matches(archive)) throw new Error('GitHub CLI checksum mismatch');
  mkdirSync(cacheDirectory, { recursive: true });
  writeFileSync(archivePath, archive);
}
mkdirSync('/githubcli');
execFileSync('tar', ['-xzf', archivePath, '--strip-components=1', '-C', '/githubcli']);
