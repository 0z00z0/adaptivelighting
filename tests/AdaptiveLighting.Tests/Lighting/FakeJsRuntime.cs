using Microsoft.JSInterop;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Stands in for <see cref="IJSRuntime"/> in a static-HTML render, where nothing runs a browser. Every
/// call fails as <see cref="JSException"/> — a component that reaches for JS while it is a component under test is
/// expected to treat that as survivable, the same as it would a browser that has gone away.</summary>
public sealed class FakeJsRuntime : IJSRuntime
{
	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
		throw new JSException($"No browser in a static render: '{identifier}' was not called.");

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
		throw new JSException($"No browser in a static render: '{identifier}' was not called.");
}
