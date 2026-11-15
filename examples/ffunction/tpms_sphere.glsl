// Lattice-filled sphere: TPMS infill (surface 1 gyroid, 2 Schwarz P, 3 diamond, 4 Neovius) inside a solid skin,
// cut open at the front (cutDepth 0 = closed) to show the infill.
param float surface = 1.0;
param float cell = 8.0;       // lattice period
param float thickness = 0.8;  // lattice wall thickness
param float radius = 20.0;
param float skin = 1.5;       // shell thickness
param float cutDepth = 20.0;  // how far the front is cut away
param float fillet = 1.0;     // blend between infill and skin

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
    float lattice = abs(tpms(p * w, surface) / w) - 0.5 * thickness;
    float ball = length(p) - radius;
    float shell = max(ball, -(ball + skin));
    float d = smin(max(lattice, ball + skin * 0.5), shell, fillet);
    return max(d, p.z - (radius - cutDepth));
}
