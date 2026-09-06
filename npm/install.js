const fs = require('fs');
const path = require('path');
const https = require('https');
const { execSync } = require('child_process');

const version = require('../package.json').version;
const platform = process.platform;
const arch = process.arch;

const targets = {
  'win32-x64': { file: 'csmesh-win-x64.zip', bin: 'csmesh.exe' },
  'linux-x64': { file: 'csmesh-linux-x64.tar.gz', bin: 'csmesh' },
  'darwin-arm64': { file: 'csmesh-osx-arm64.tar.gz', bin: 'csmesh' }
};

const key = `${platform}-${arch}`;
const target = targets[key];

if (!target) {
  console.error(`Unsupported platform/architecture: ${key}`);
  process.exit(1);
}

const binDir = path.join(__dirname, '..', 'bin');
if (!fs.existsSync(binDir)) fs.mkdirSync(binDir, { recursive: true });

const url = `https://github.com/nRafinia/CsMesh/releases/download/v${version}/${target.file}`;
const tempArchive = path.join(binDir, target.file);

console.log(`Downloading ${target.file} from ${url}...`);

function download(url, dest, cb) {
  https.get(url, (res) => {
    if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
      return download(res.headers.location, dest, cb);
    }
    if (res.statusCode !== 200) {
      return cb(new Error(`Failed with status ${res.statusCode}`));
    }
    const file = fs.createWriteStream(dest);
    res.pipe(file);
    file.on('finish', () => file.close(cb));
  }).on('error', cb);
}

download(url, tempArchive, (err) => {
  if (err) {
    console.error('Download failed:', err.message);
    process.exit(1);
  }

  if (target.file.endsWith('.zip')) {
    execSync(`powershell -Command "Expand-Archive -Path '${tempArchive}' -DestinationPath '${binDir}' -Force"`);
  } else {
    execSync(`tar -xzf "${tempArchive}" -C "${binDir}"`);
  }

  fs.unlinkSync(tempArchive);
  const binPath = path.join(binDir, target.bin);
  if (platform !== 'win32') fs.chmodSync(binPath, 0o755);
  console.log('Installation finished successfully.');
});