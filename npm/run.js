#!/usr/bin/env node
const path = require('path');
const { spawn } = require('child_process');

const binName = process.platform === 'win32' ? 'csmesh.exe' : 'csmesh';
const binPath = path.join(__dirname, binName);

const proc = spawn(binPath, process.argv.slice(2), { stdio: 'inherit' });
proc.on('exit', (code) => process.exit(code || 0));