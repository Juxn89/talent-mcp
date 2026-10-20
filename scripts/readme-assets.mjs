// Generates the README artwork in docs/readme/: hero banner, metrics strip, feature icons and the
// divider, as SVG in a light and a dark variant. Plain Node, no dependencies:
//
//   node scripts/readme-assets.mjs
//
// GitHub shows README images through <img>: plain SVG, no external fonts or scripts. CSS animations
// inside the SVG do run there, and they stop under prefers-reduced-motion.
//
// The showcase screenshots next to them (showcase-light.png, showcase-dark.png) are not generated: they
// are captures of MCP Inspector 2.9.0 against the running server, the confirmation reject_candidate asks
// for. The full set is in docs/demo.
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const out = fileURLToPath(new URL('../docs/readme/', import.meta.url));
await mkdir(out, { recursive: true });

const brand = { blue: '#2563eb', sky: '#38bdf8', amber: '#f59e0b', red: '#ef4444', green: '#22c55e' };

const themes = {
	dark: { bg: '#080c14', panel: '#101827', card: '#141e30', border: '#22314a', text: '#f1f5f9', muted: '#94a3b8', line: '#2a3a55', dot: '#ffffff', dotOpacity: 0.06, orb: 0.3 },
	light: { bg: '#f7faff', panel: '#ffffff', card: '#ffffff', border: '#dbe4f0', text: '#0f172a', muted: '#475569', line: '#cbd5e1', dot: '#2563eb', dotOpacity: 0.08, orb: 0.16 },
};

const sans = `'Segoe UI', Inter, -apple-system, BlinkMacSystemFont, 'Helvetica Neue', Arial, sans-serif`;
const mono = `'Cascadia Code', 'SFMono-Regular', Consolas, Menlo, monospace`;
const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function defs(t) {
	return `
	<linearGradient id="brand" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${brand.sky}"/><stop offset=".55" stop-color="${brand.blue}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<linearGradient id="metric" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${brand.sky}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<radialGradient id="orbBlue"><stop offset="0" stop-color="${brand.blue}" stop-opacity="${t.orb}"/><stop offset="1" stop-color="${brand.blue}" stop-opacity="0"/></radialGradient>
	<radialGradient id="orbAmber"><stop offset="0" stop-color="${brand.amber}" stop-opacity="${t.orb * 0.6}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></radialGradient>
	<pattern id="dots" width="22" height="22" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1.1" fill="${t.dot}" fill-opacity="${t.dotOpacity}"/></pattern>`;
}

// ── Hero: what it does, drawn — an agent asks for a destructive tool and the server asks back ─────
function hero(name) {
	const t = themes[name];
	const W = 1280;
	const H = 460;
	const accent = name === 'dark' ? brand.sky : brand.blue;
	const chips = ['MCP 2026-07-28', 'OAuth 2.1 + PKCE', 'Stateless HTTP', 'stdio dotnet tool', 'OpenTelemetry'];
	let x = 72;
	const chipSvg = chips
		.map((chip) => {
			const w = chip.length * 8.2 + 30;
			const g = `<g transform="translate(${x} 352)"><rect width="${w}" height="32" rx="16" fill="${t.panel}" stroke="${t.border}"/><text x="${w / 2}" y="21" text-anchor="middle" font-family="${sans}" font-size="14" font-weight="600" fill="${t.text}">${chip}</text></g>`;
			x += w + 8;
			return g;
		})
		.join('');

	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-labelledby="title desc">
<title id="title">talent-mcp</title>
<desc id="desc">An MCP server in C# that gives AI agents typed, secured tools: permissions per tool, and confirmation before anything destructive.</desc>
<style>
	.flow { stroke-dasharray: 6 7; animation: flow 1.6s linear infinite; }
	.pop { opacity: 0; animation: pop .5s ease-out forwards; }
	.glow { animation: glow 2.4s ease-in-out infinite alternate; }
	@keyframes flow { to { stroke-dashoffset: -26; } }
	@keyframes pop { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
	@keyframes glow { from { opacity: .6; } to { opacity: 1; } }
	@media (prefers-reduced-motion: reduce) { .flow, .glow { animation: none; } .pop { animation: none; opacity: 1; } }
</style>
<defs>${defs(t)}
	<clipPath id="frame"><rect width="${W}" height="${H}" rx="28"/></clipPath>
	<filter id="soft" x="-20%" y="-20%" width="140%" height="160%"><feDropShadow dx="0" dy="14" stdDeviation="16" flood-color="#000" flood-opacity="${name === 'dark' ? 0.45 : 0.12}"/></filter>
</defs>
<g clip-path="url(#frame)">
	<rect width="${W}" height="${H}" fill="${t.bg}"/>
	<rect width="${W}" height="${H}" fill="url(#dots)"/>
	<circle cx="180" cy="40" r="340" fill="url(#orbBlue)"/>
	<circle cx="1080" cy="480" r="360" fill="url(#orbAmber)"/>
</g>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="28" fill="none" stroke="${t.border}"/>

<g transform="translate(72 70)">
	<rect width="290" height="30" rx="15" fill="${t.panel}" stroke="${t.border}"/>
	<circle cx="18" cy="15" r="4.5" fill="${brand.sky}" class="glow"/>
	<text x="31" y="20" font-family="${sans}" font-size="12.5" font-weight="700" letter-spacing=".1em" fill="${t.muted}">OPEN SOURCE · .NET 10 · APACHE 2.0</text>
</g>
<text x="68" y="172" font-family="${sans}" font-size="70" font-weight="800" letter-spacing="-.03em" fill="${t.text}">talent<tspan fill="url(#brand)">-mcp</tspan></text>
<text x="72" y="230" font-family="${sans}" font-size="24" font-weight="600" fill="${t.text}">Gives AI agents typed, secured tools.</text>
<text x="72" y="268" font-family="${sans}" font-size="20" fill="${t.muted}">A permission per tool. Confirmation before anything destructive.</text>
<text x="72" y="300" font-family="${sans}" font-size="20" fill="${t.muted}">No model inside: deterministic results at zero cost.</text>
${chipSvg}

<!-- The agent asks for a destructive tool -->
<g transform="translate(720 70)" filter="url(#soft)">
	<rect width="236" height="176" rx="18" fill="${t.card}" stroke="${t.border}"/>
	<rect x="18" y="18" width="26" height="26" rx="8" fill="${t.panel}" stroke="${t.border}"/>
	<path d="M31 24l1.6 4.2L37 30l-4.4 1.8L31 36l-1.6-4.2L25 30l4.4-1.8Z" fill="${accent}"/>
	<text x="54" y="36" font-family="${sans}" font-size="13" font-weight="700" fill="${t.text}">AI agent</text>
	<rect x="18" y="60" width="200" height="36" rx="12" fill="${brand.blue}"/>
	<text x="32" y="83" font-family="${sans}" font-size="12.5" fill="#fff">Reject Clara, she accepted…</text>
	<text x="18" y="124" font-family="${mono}" font-size="11.5" fill="${t.muted}">tools/call</text>
	<text x="18" y="144" font-family="${mono}" font-size="12.5" font-weight="700" fill="${t.text}">reject_candidate</text>
	<rect x="18" y="152" width="150" height="2" rx="1" fill="${brand.red}" fill-opacity=".6"/>
</g>

<!-- The call reaches the server -->
<path d="M956 158 C 990 158, 980 120, 1010 120" fill="none" stroke="${accent}" stroke-width="2.2" class="flow"/>
<circle cx="956" cy="158" r="4.5" fill="${accent}"/>

<!-- The server answers with a question, not a deletion -->
<g transform="translate(1000 54)" filter="url(#soft)">
	<rect width="236" height="206" rx="18" fill="${t.card}" stroke="${t.border}"/>
	<rect x="18" y="18" width="26" height="26" rx="8" fill="${brand.blue}"/>
	<path d="M25 27h12M25 31h12M25 35h8" stroke="#fff" stroke-width="2" stroke-linecap="round"/>
	<text x="54" y="36" font-family="${sans}" font-size="13" font-weight="700" fill="${t.text}">talent-mcp</text>
	<g class="pop" style="animation-delay:.3s">
		<rect x="18" y="58" width="112" height="22" rx="11" fill="${brand.amber}" fill-opacity="${name === 'dark' ? 0.22 : 0.16}"/>
		<text x="74" y="73" text-anchor="middle" font-family="${mono}" font-size="11" font-weight="700" fill="${name === 'dark' ? brand.amber : '#b45309'}">input_required</text>
		<text font-family="${sans}" font-size="12.5" fill="${t.text}">
			<tspan x="18" y="104">Reject this candidate?</tspan>
			<tspan x="18" y="122">This cannot be undone.</tspan>
		</text>
		<rect x="18" y="138" width="96" height="30" rx="9" fill="${brand.blue}"/>
		<text x="66" y="158" text-anchor="middle" font-family="${sans}" font-size="12" font-weight="700" fill="#fff">Confirm</text>
		<rect x="122" y="138" width="96" height="30" rx="9" fill="${t.panel}" stroke="${t.border}"/>
		<text x="170" y="158" text-anchor="middle" font-family="${sans}" font-size="12" font-weight="700" fill="${t.text}">Decline</text>
		<text x="18" y="190" font-family="${mono}" font-size="10.5" fill="${t.muted}">scope: talent.candidates.reject</text>
	</g>
</g>

<!-- Every call is traced. The animated group is nested: its transform: none would otherwise replace the translate. -->
<path d="M1118 260 L 1118 294" fill="none" stroke="${accent}" stroke-width="2.2" class="flow"/>
<g transform="translate(1000 294)"><g class="pop" style="animation-delay:1s">
	<rect width="236" height="40" rx="20" fill="${t.panel}" stroke="${t.border}"/>
	<circle cx="22" cy="20" r="12" fill="${brand.blue}" fill-opacity=".15"/>
	<path d="M14 20h4l2.5-5 3.5 10 2.5-5h3" fill="none" stroke="${accent}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
	<text x="42" y="25" font-family="${sans}" font-size="12.5" font-weight="700" fill="${t.text}">Traced</text>
	<text x="92" y="25" font-family="${sans}" font-size="12" fill="${t.muted}">· per-tool metrics</text>
</g></g>
</svg>
`;
}

// ── Metrics: each one traceable to CI, the changelog or the code ───────────────────────────────────
function metrics(name) {
	const t = themes[name];
	const W = 1280;
	const H = 150;
	const items = [
		['6', 'typed tools', 'each behind its own OAuth scope'],
		['323 ms', 'cold start of the dotnet tool', 'measured in CI'],
		['~1 s', 'to recover a Postgres restart', 'Tasks store · 3 CI runs'],
		['0', 'LLM calls', 'no API key · no model cost'],
	];
	const col = W / items.length;
	const cells = items
		.map(
			([value, label, note], i) => `
	<g transform="translate(${col * i + col / 2} 0)">
		<text y="64" text-anchor="middle" font-family="${sans}" font-size="40" font-weight="800" letter-spacing="-.02em" fill="url(#metric)">${escape(value)}</text>
		<text y="94" text-anchor="middle" font-family="${sans}" font-size="16" font-weight="600" fill="${t.text}">${escape(label)}</text>
		<text y="116" text-anchor="middle" font-family="${sans}" font-size="13.5" fill="${t.muted}">${escape(note)}</text>
	</g>${i > 0 ? `<line x1="${col * i}" y1="36" x2="${col * i}" y2="114" stroke="${t.border}"/>` : ''}`,
		)
		.join('');
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="6 typed tools, each behind its own OAuth scope. 323 ms cold start of the dotnet tool, measured in CI. About 1 second for the Tasks store to recover a Postgres restart, over 3 CI runs. 0 LLM calls: no API key and no model cost.">
<defs>${defs(t)}</defs>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="22" fill="${t.panel}" stroke="${t.border}"/>
${cells}
</svg>
`;
}

// ── Feature icons: Lucide glyphs (ISC license) on a gradient tile ──────────────────────────────────
const glyphs = {
	handle: '<path d="m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4"/><path d="m21 2-9.6 9.6"/><circle cx="7.5" cy="15.5" r="5.5"/>',
	confirm: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/><path d="M12 9v4"/><path d="M12 17h.01"/>',
	scopes: '<rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
	tasks: '<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5V19A9 3 0 0 0 21 19V5"/><path d="M3 12A9 3 0 0 0 21 12"/>',
	explain: '<path d="m3 17 2 2 4-4"/><path d="m3 7 2 2 4-4"/><path d="M13 6h8"/><path d="M13 12h8"/><path d="M13 18h8"/>',
	trace: '<path d="M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"/>',
	hosts: '<rect width="20" height="8" x="2" y="2" rx="2" ry="2"/><rect width="20" height="8" x="2" y="14" rx="2" ry="2"/><line x1="6" x2="6.01" y1="6" y2="6"/><line x1="6" x2="6.01" y1="18" y2="18"/>',
	tests: '<path d="M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2"/><path d="M6.453 15h11.094"/><path d="M8.5 2h7"/>',
	package: '<path d="M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z"/><path d="M12 22V12"/><path d="m3.3 7 7.703 4.734a2 2 0 0 0 1.994 0L20.7 7"/>',
};

const tiles = {
	handle: ['#1e3a8a', '#60a5fa'],
	confirm: ['#b45309', '#fbbf24'],
	scopes: ['#334155', '#64748b'],
	tasks: ['#0e7490', '#22d3ee'],
	explain: ['#065f46', '#10b981'],
	trace: ['#4338ca', '#818cf8'],
	hosts: ['#0f766e', '#2dd4bf'],
	tests: ['#7c2d12', '#f97316'],
	package: ['#5b21b6', '#a78bfa'],
};

function iconSvg(name) {
	const [from, to] = tiles[name];
	return `<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56" viewBox="0 0 56 56" aria-hidden="true">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs>
<rect width="56" height="56" rx="16" fill="url(#g)"/>
<rect x=".75" y=".75" width="54.5" height="54.5" rx="15.25" fill="none" stroke="#fff" stroke-opacity=".18" stroke-width="1.5"/>
<g transform="translate(16 16)" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${glyphs[name]}</g>
</svg>
`;
}

const divider = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="24" viewBox="0 0 1280 24" aria-hidden="true">
<defs><linearGradient id="d" x1="0" x2="1"><stop offset="0" stop-color="${brand.sky}" stop-opacity="0"/><stop offset=".3" stop-color="${brand.blue}"/><stop offset=".7" stop-color="${brand.amber}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></linearGradient></defs>
<rect y="11" width="1280" height="2" rx="1" fill="url(#d)"/>
<rect x="632" y="4" width="16" height="16" rx="5" fill="${brand.blue}"/><path d="M636 12h2l1.5-3 2 6 1.5-3h1" fill="none" stroke="#fff" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
`;

for (const name of ['dark', 'light']) {
	await writeFile(`${out}hero-${name}.svg`, hero(name));
	await writeFile(`${out}metrics-${name}.svg`, metrics(name));
}

for (const name of Object.keys(glyphs)) {
	await writeFile(`${out}icon-${name}.svg`, iconSvg(name));
}

await writeFile(`${out}divider.svg`, divider);
console.log(`README artwork written to ${out}`);
