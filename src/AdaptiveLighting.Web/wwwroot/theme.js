/*
	Served at _content/AdaptiveLighting.Web/theme.js, and loaded from <head> WITHOUT defer or async on purpose.

	This is a Blazor Server app: the server paints the page before any circuit exists, so a theme read from
	localStorage in OnAfterRenderAsync arrives after the first paint and the page visibly repaints from the
	device's colours to the chosen ones. A classic script in <head> blocks the parser until it has run, so
	data-theme is on <html> before the body is parsed and there is nothing to repaint.

	A file rather than an inline <script>: it matches how this library already ships app.css, it costs nothing
	on a LAN, and it survives a host that adds a content policy forbidding inline script — which would break an
	inline version silently, in production only.

	The allow-list is not written here. It arrives on the script tag as data-themes, rendered from
	AppThemes in C#, so the ids a browser may hold have exactly one definition.
*/
(function () {
	'use strict';

	var KEY = 'adaptive-lighting-theme';
	var script = document.currentScript;
	var allowed = (script && script.dataset.themes ? script.dataset.themes : '').split(' ').filter(Boolean);

	/* Storage throws rather than returning null in a browser with site data blocked, and a theme is not worth
	   taking the page down for. */
	function read() {
		try {
			return window.localStorage.getItem(KEY);
		} catch (e) {
			return null;
		}
	}

	function write(id) {
		try {
			window.localStorage.setItem(KEY, id);
		} catch (e) {
			/* The choice holds for this page and is forgotten on the next. Better than an unhandled throw. */
		}
	}

	/* An id naming a theme this build no longer ships must fall back to the device, not to a data-theme value
	   with no palette behind it: every token would come from the bare :root block and a light desk would get a
	   dark page. */
	function paint(id) {
		if (id && allowed.indexOf(id) >= 0) {
			document.documentElement.setAttribute('data-theme', id);
		} else {
			document.documentElement.removeAttribute('data-theme');
		}
	}

	paint(read());

	window.adaptiveLightingTheme = {
		/* What the picker should show as selected once the circuit is up. Unvalidated on purpose — the server
		   resolves it, so the fallback rule lives in one place. */
		stored: read,

		apply: function (id) {
			write(id);
			paint(id);
		}
	};
})();
