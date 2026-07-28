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
						{ label: 'How it works', slug: 'overview' },
						{ label: 'How to use it', slug: 'user-guide' },
					],
				},
				{
					label: 'Reference',
					items: [
						{ label: 'Settings reference', slug: 'configuration' },
						{ label: 'Example configuration', slug: 'example-config' },
					],
				},
			],
		}),
	],
});
