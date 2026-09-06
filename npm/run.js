#!/usr/bin/env node

const path = require('path');
const fs = require('fs');
const { spawn } = require('child_process');

const binName = process.platform === 'win32' ? 'csmesh.exe' : 'csmesh';
const binPath = path.join(__dirname, binName);

if (!fs.existsSync(binPath)) {
  console.error(`[csmesh] Native executable not found at: ${binPath}`);
  console.error('Try reinstalling the package: npm install -g @nrafinia/csmesh');
  process.exit(1);
}

const child = spawn(binPath, process.argv.slice(2), {
  stdio: 'inherit',
  windowsHide: true
});

child.on('error', (err) => {
  console.error(`[csmesh] Failed to launch binary: ${err.message}`);
  process.exit(1);
});

child.on('exit', (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
  } else {
    process.exit(code !== null ? code : 0);
  }
});