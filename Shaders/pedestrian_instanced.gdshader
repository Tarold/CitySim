shader_type canvas_item;

// Hardware-instanced pedestrian capsule/dot shader for MultiMeshInstance2D.
// Instance COLOR.rgb carries clothing or pedestrian flow color.

varying vec4 v_ped_color;

void vertex() {
	v_ped_color = COLOR;
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;

	// Main shoulder/body circle centered slightly behind origin, plus forward head circle at (+0.32, 0.0)
	float bodyDist = length(p - vec2(-0.10, 0.0)) - 0.72;
	float headDist = length(p - vec2(0.38, 0.0)) - 0.42;
	float combinedDist = min(bodyDist, headDist);

	if (combinedDist > 0.05) {
		discard;
	}

	vec3 col = v_ped_color.rgb;
	if (headDist < bodyDist) {
		// Lighter head / walking direction indicator
		col = mix(v_ped_color.rgb, vec3(1.0, 0.89, 0.77), 0.65);
	}

	float alpha = (1.0 - smoothstep(-0.05, 0.05, combinedDist)) * v_ped_color.a;
	COLOR = vec4(col, alpha);
}
