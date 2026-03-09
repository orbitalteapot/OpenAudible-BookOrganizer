/**
 * Publishes the .NET backend as a self-contained single-file binary
 * for the specified platform (win-x64, linux-x64, osx-x64, osx-arm64).
 *
 * Usage: node scripts/publish-backend.js [rid]
 *   rid defaults to the current platform
 */
const { execSync } = require('child_process');
const path = require('path');
const fs = require('fs');

const ridMap = {
  win32: 'win-x64',
  linux: 'linux-x64',
  darwin: 'osx-x64', // override with osx-arm64 for Apple Silicon
};

const requestedRid = process.argv[2] || ridMap[process.platform] || 'win-x64';
const projectDir = path.resolve(__dirname, '../../ManagerApi');
const outputDir = path.resolve(__dirname, `../backend-bin/${requestedRid}`);

console.log(`Publishing backend for ${requestedRid}...`);

// Clean previous output
if (fs.existsSync(outputDir)) {
  fs.rmSync(outputDir, { recursive: true });
}

const cmd = [
  'dotnet', 'publish',
  `"${projectDir}"`,
  '-c', 'Release',
  '-r', requestedRid,
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-o', `"${outputDir}"`,
].join(' ');

console.log(`> ${cmd}\n`);
execSync(cmd, { stdio: 'inherit' });

console.log(`\nBackend published to: ${outputDir}`);
