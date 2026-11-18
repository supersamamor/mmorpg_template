// The near-only outline (ADR-003 point 2, T-222): an inverted hull, drawn as the material overlay of a character, monster or interactable object close to the
// camera target, so it costs one more draw call for that object and none for anything else. Each vertex moves out along its outline direction, the TANGENT the
// generated models carry (the normal averaged over every vertex at the same place, so the hull stays closed at hard edges); a mesh without one uses its normal.
// The width is in pixels of a 720-pixel-high view and scales with the view's height, so it reads the same on every screen.
shader_type spatial;
render_mode unshaded, cull_front, shadows_disabled, specular_disabled;

uniform vec4 outline_color : source_color = vec4(0.165, 0.133, 0.125, 1.0); // a warm near-black, never pure black
uniform float width_px = 1.6;

void vertex() {
	vec3 along = dot(TANGENT, TANGENT) > 0.01 ? TANGENT : NORMAL;
	vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	vec2 screen = (PROJECTION_MATRIX * vec4(mat3(MODELVIEW_MATRIX) * along, 0.0)).xy;
	float len = length(screen);
	if (len > 1e-6) {
		screen /= len;
	}
	clip.xy += screen * (width_px * VIEWPORT_SIZE.y / 720.0) * 2.0 / VIEWPORT_SIZE * clip.w;
	POSITION = clip;
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
