namespace AdaptiveLighting.Extensions;

/// <summary>
///     The Home Assistant domains the old fluent style enumerated, kept for continuity with
///     <see cref="EntityIdExtensions.DomainEnum"/>. String domains cover every current call site; this exists
///     only for code that prefers an enum.
/// </summary>
public enum EntityDomain
{
	unknown,
	light,
	media_player,
	automation,
	binary_sensor,
	bodymiscale,
	button,
	camera,
	climate,
	device_tracker,
	input_boolean,
	input_select,
	input_text,
	number,
	person,
	scene,
	select,
	sensor,
	stt,
	sun,
	@switch, //switch is reserved word
	timer,
	todo,
	tts,
	update,
	wake_word,
	weather,
	zone
}
