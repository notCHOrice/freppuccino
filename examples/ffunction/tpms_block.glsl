// TPMS lattice block: slide surface (1 gyroid, 2 Schwarz P, 3 diamond, 4 Neovius); walls thin toward the top;
// network > 0.5 fills one side solid (strut network) instead of a sheet.
param float surface = 1.0;
param float cell = 10.0;     // lattice period
param float thickBot = 1.6;  // wall thickness at the bottom
param float thickTop = 0.5;  // ... at the top
param float network = 0.0;
param float halfH = 20.0;

float tpms(vec3 q, float kind) {
    if (kind < 1.5) return dot(sin(q), cos(q.yzx)) / 1.5;
    if (kind < 2.5) return (cos(q.x) + cos(q.y) + cos(q.z)) / 1.7;
    if (kind < 3.5) {
        vec3 s = sin(q), c = cos(q);
        return (s.x * s.y * s.z + s.x * c.y * c.z + c.x * s.y * c.z + c.x * c.y * s.z) / 1.5;
    }
    return (3.0 * (cos(q.x) + cos(q.y) + cos(q.z)) + 4.0 * cos(q.x) * cos(q.y) * cos(q.z)) / 6.0;
}

float sdf(vec3 p) {
    float w = 6.2831853 / cell;
    float g = tpms(p * w, surface) / w;
    float t = mix(thickBot, thickTop, clamp((p.y + halfH) / (2.0 * halfH), 0.0, 1.0));
    return network > 0.5 ? g - 0.5 * t : abs(g) - 0.5 * t;
}
