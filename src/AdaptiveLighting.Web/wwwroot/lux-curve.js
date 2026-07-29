/*
	The pointer half of the daylight-brightness chart.

	Everything this file knows is where a finger is, as a fraction of the plot's box; which handle that grabs and
	what the number becomes is C#'s, where it can be tested. Three things have to happen in the browser, and none
	of them can be expressed from .NET:

	  · setPointerCapture, so a drag that leaves the plot keeps steering it instead of stopping at the edge;
	  · touch-action: none plus preventDefault, or a touch drag scrolls the page and the chart never moves;
	  · one pointer API for mouse, pen and finger, so there is one code path rather than three.

	It also holds the circuit's backpressure. Every report is a round trip over a Blazor Server connection, and a
	pointer stream is sixty a second: sent unthrottled they queue, and the curve then follows the hand several
	seconds late. At most one report is ever outstanding; a move that arrives while one is in flight replaces
	whichever move was waiting, so the server always gets the newest position and never a backlog of stale ones.

	Like theme.js, this refuses to take the page down: a browser without pointer events, or an element that has
	not been laid out yet, simply gets no drag — the numbers under the chart still set every value it sets.
*/
window.adaptiveLightingCurve = (function () {
	const watched = new WeakMap();

	// The pointer as a fraction of the surface's own box. Null while the element has no box — during a render, or
	// while the chart is inside a collapsed fold — because dividing by zero would report the top-left corner and
	// silently drag the handle there.
	function at(surface, event) {
		const box = surface.getBoundingClientRect();

		if (!(box.width > 0) || !(box.height > 0)) {
			return null;
		}

		return {
			x: Math.min(1, Math.max(0, (event.clientX - box.left) / box.width)),
			y: Math.min(1, Math.max(0, (event.clientY - box.top) / box.height))
		};
	}

	function watch(surface, owner) {
		if (!surface || !owner || watched.has(surface)) {
			return;
		}

		let dragging = false;
		let inFlight = false;
		let pending = null;

		function push(method, point) {
			if (inFlight) {
				// A grab decides which handle the rest of the gesture steers, so it is never replaced by a move
				// that overtook it; every other collision keeps the newest position.
				if (!(pending && pending.method === 'Grab' && method === 'Move')) {
					pending = { method: method, point: point };
				}

				return;
			}

			inFlight = true;

			owner.invokeMethodAsync(method, point.x, point.y)
				.catch(function () {
					// The circuit went away, or the component was disposed mid-gesture. Stop steering rather than
					// retrying into a connection that is not there.
					dragging = false;
					pending = null;
				})
				.finally(function () {
					inFlight = false;

					const next = pending;
					pending = null;

					if (next && dragging) {
						push(next.method, next.point);
					}
				});
		}

		function down(event) {
			// Primary button only; a right-click is a context menu, not a drag.
			if (typeof event.button === 'number' && event.button !== 0) {
				return;
			}

			const point = at(surface, event);

			if (!point) {
				return;
			}

			dragging = true;
			pending = null;

			try {
				surface.setPointerCapture(event.pointerId);
			} catch (error) {
				// No capture available: the drag still works inside the plot, which is where it usually stays.
			}

			event.preventDefault();
			push('Grab', point);
		}

		function move(event) {
			if (!dragging) {
				return;
			}

			const point = at(surface, event);

			if (!point) {
				return;
			}

			event.preventDefault();
			push('Move', point);
		}

		function stop(event) {
			if (!dragging) {
				return;
			}

			dragging = false;
			pending = null;

			try {
				surface.releasePointerCapture(event.pointerId);
			} catch (error) {
				// Already released, or never captured.
			}

			owner.invokeMethodAsync('Drop').catch(function () { });
		}

		surface.addEventListener('pointerdown', down);
		surface.addEventListener('pointermove', move);
		surface.addEventListener('pointerup', stop);
		surface.addEventListener('pointercancel', stop);

		watched.set(surface, function () {
			surface.removeEventListener('pointerdown', down);
			surface.removeEventListener('pointermove', move);
			surface.removeEventListener('pointerup', stop);
			surface.removeEventListener('pointercancel', stop);
		});
	}

	function release(surface) {
		if (!surface) {
			return;
		}

		const teardown = watched.get(surface);

		if (teardown) {
			teardown();
			watched.delete(surface);
		}
	}

	return { watch: watch, release: release };
})();
