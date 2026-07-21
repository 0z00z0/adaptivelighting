// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// The docs site. Content is the markdown under src/content/docs — the same documents that used to sit in
// the repository's docs/ folder, moved here rather than copied so there is exactly one source of truth.
export default defineConfig({
	site: 'https://adaptivelighting.netlify.app',
	integrations: [
		starlight({
			title: 'AdaptiveLighting',
			description:
				'Motion- and daylight-driven lighting for Home Assistant, as a NetDaemon library.',
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/0z00z0/adaptivelighting' },
			],
			editLink: {
				baseUrl: 'https://github.com/0z00z0/adaptivelighting/edit/main/website/',
			},
			lastUpdated: true,
			sidebar: [
				{
					label: 'Start here',
					items: [
						{ label: 'User guide', slug: 'user-guide' },
						{ label: 'Overview', slug: 'overview' },
					],
				},
				{
					label: 'Configuration',
					items: [
						{ label: 'Configuration reference', slug: 'configuration' },
						{ label: 'Example configuration', slug: 'example-config' },
					],
				},
				{
					label: 'Internals',
					items: [
						{ label: 'Architecture', slug: 'architecture' },
						{ label: 'The web UI', slug: 'web-ui' },
					],
				},
			],
		}),
	],
});
