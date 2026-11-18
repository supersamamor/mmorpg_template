// The storybook toon light (ADR-003 point 1, T-222): one warm directional light in three steps, shadow, mid and lit. The shadow map folds into the same
// steps (a shadowed point is in the shadow step), and the flat ambient of the environment lights the shadow step. No specular.
// Included by the toon material and by the map scenes' ground, so characters, props and terrain share one ramp.
uniform float toon_mid_from : hint_range(-1.0, 1.0) = 0.0;   // N·L (times the shadow) above this gets the mid step
uniform float toon_lit_from : hint_range(-1.0, 1.0) = 0.4;   // and above this the lit step
uniform float toon_mid_level : hint_range(0.0, 1.0) = 0.5;   // the share of the light the mid step gets
uniform float toon_edge : hint_range(0.0, 0.2) = 0.015;      // a hair of smoothing on the step edges, so they do not shimmer

void light() {
	float ndl = dot(NORMAL, LIGHT) * ATTENUATION;
	float mid = smoothstep(toon_mid_from - toon_edge, toon_mid_from + toon_edge, ndl);
	float lit = smoothstep(toon_lit_from - toon_edge, toon_lit_from + toon_edge, ndl);
	DIFFUSE_LIGHT += LIGHT_COLOR * (toon_mid_level * mid + (1.0 - toon_mid_level) * lit) / PI;
}
