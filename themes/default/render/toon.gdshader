// The storybook toon material (ADR-003, T-222): flat colour times the vertex colours (the generated models and GearMesher's merged characters keep their colours
// there, so a dressed character is one surface), optionally times one atlas texture (real art, ADR-003 point 5), lit in three steps by toon_light.
// A vertex with alpha 0 glows: it is drawn unlit, at its full colour (crystals, runes; ADR-003 point 1). The client sets albedo, albedo_texture and
// use_vertex_color from the material it replaces; the rest is this theme's look.
shader_type spatial;
render_mode cull_back, specular_disabled;

#include "toon_light.gdshaderinc"

uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform bool use_vertex_color = true;

varying float glow;

void vertex() {
	glow = use_vertex_color ? 1.0 - COLOR.a : 0.0;
}

void fragment() {
	vec3 base = albedo.rgb * texture(albedo_texture, UV).rgb;
	if (use_vertex_color) base *= COLOR.rgb;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
	if (glow > 0.5) {
		ALBEDO = vec3(0.0);
		EMISSION = base;
	} else {
		ALBEDO = base;
	}
}
