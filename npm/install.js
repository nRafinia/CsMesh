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
if (!fs.existsSync(binDir)) {
  fs.mkdirSync(binDir, { recursive: true });
}

const releaseTag = version.startsWith('v') ? version : `v${version}`;
const url = `https://github.com/nRafinia/CsMesh/releases/download/${releaseTag}/${target.file}`;
const tempArchive = path.join(binDir, target.file);

console.log(`Downloading ${target.file} from ${url}...`);

function download(fileUrl, destPath, callback) {
  https.get(fileUrl, (res) => {
    if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
      return download(res.headers.location, destPath, callback);
    }
    if (res.statusCode !== 200) {
      return callback(new Error(`Download failed with status ${res.statusCode}`));
    }
    const fileStream = fs.createWriteStream(destPath);
    res.pipe(fileStream);
    fileStream.on('finish', () => fileStream.close(callback));
  }).on('error', callback);
}

download(url, tempArchive, (err) => {
  if (err) {
    console.error('Download error:', err.message);
    process.exit(1);
  }

  try {
    if (target.file.endsWith('.zip')) {
      execSync(`powershell -Command "Expand-Archive -Path '${tempArchive}' -DestinationPath '${binDir}' -Force"`);
    } else {
      execSync(`tar -xzf "${tempArchive}" -C "${binDir}"`);
    }
  } catch (extractErr) {
    console.error('Extraction failed:', extractErr.message);
    process.exit(1);
  } finally {
    if (fs.existsSync(tempArchive)) {
      fs.unlinkSync(tempArchive);
    }
  }

  const binPath = path.join(binDir, target.bin);
  if (platform !== 'win32' && fs.existsSync(binPath)) {
    fs.chmodSync(binPath, 0o755);
  }

  console.log('csmesh binary ready.');
});