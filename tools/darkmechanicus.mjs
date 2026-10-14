// Use the installed application's supported MCP interface; never edit its storage directly.
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
const sdk = process.env.DM_SDK_ROOT || 'C:/Users/davgo/Documents/GitHub/DarkMechanicus/node_modules/@modelcontextprotocol/sdk/dist/esm';
const { Client } = await import(pathToFileURL(`${sdk}/client/index.js`));
const { StdioClientTransport } = await import(pathToFileURL(`${sdk}/client/stdio.js`));
const config = JSON.parse(readFileSync(new URL('../.mcp.json', import.meta.url))).mcpServers.darkmechanicus;
const client = new Client({ name: 'mnbl-implementation', version: '1.0.0' });
const transport = new StdioClientTransport({ command: config.command, args: config.args.map(x => x === 'Claude Code' ? 'Codex FWGC implementation' : x), env: { ...process.env, ...config.env }, stderr: 'inherit' });
try {
  await client.connect(transport);
  await client.callTool({ name: 'get_capabilities', arguments: {} });
  if (process.argv[2] === 'list') console.log(JSON.stringify(await client.listTools(), null, 2));
  else {
    const request = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    for (const item of Array.isArray(request) ? request : [request]) {
      const result = await client.callTool(item);
      console.log(JSON.stringify(result.structuredContent || result, null, 2));
      if (result.isError || result.structuredContent?.ok === false) process.exitCode = 1;
    }
  }
} finally { await client.close(); }
