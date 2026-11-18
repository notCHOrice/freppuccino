export type Vec3 = [number, number, number]

export interface FunctionExample {
  name: string
  title: string
  description: string
  code: string
  half: Vec3
  lipschitz: number
  note?: string
}

const files = import.meta.glob('../../../../examples/ffunction/*.glsl', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

const BOXES: Record<string, { half: Vec3; lipschitz: number; note?: string }> = {
  tpms_block: { half: [20, 20, 20], lipschitz: 1.5 },
  tpms_sphere: { half: [21, 21, 21], lipschitz: 1.5 },
  voronoi_foam: { half: [23, 16, 16], lipschitz: 1.1 },
  gyroid: {
    half: [25, 21, 19],
    lipschitz: 2.6,
    note: 'gyroid.3dm intersects it with an ellipsoid (center [0,0,21], radii [24.5,17.5,21]): combine op=intersect, k≈1.',
  },
  helicatenoid: { half: [31, 29, 31], lipschitz: 1.6 },
  michell_bracket: { half: [35, 28, 7], lipschitz: 1.6 },
  nautilus: { half: [34, 25, 16], lipschitz: 1.5 },
  scherk_tower: { half: [26, 34, 22], lipschitz: 1.7 },
  tet_goo: { half: [25, 27, 18], lipschitz: 1.25 },
  turing_vase: { half: [22, 30.5, 22], lipschitz: 2.6 },
}

const ORDER = Object.keys(BOXES)

const STARTERS: FunctionExample[] = [
  {
    name: 'starter_sphere',
    title: 'Starter: sphere',
    description: 'The smallest valid function: a sphere with a radius param.',
    half: [12, 12, 12],
    lipschitz: 1,
    code: `param float radius = 10.0;

float sdf(vec3 p) {
    return length(p) - radius;
}
`,
  },
  {
    name: 'starter_gyroid',
    title: 'Starter: gyroid',
    description: 'Gyroid sheet filling its box (the FFunction command default).',
    half: [20, 20, 20],
    lipschitz: 1.5,
    code: `param float cell = 10.0;      // period of the lattice
param float thickness = 1.0;  // wall thickness

float sdf(vec3 p) {
    float w = 6.2831853 / cell;
    vec3 q = p * w;
    float g = sin(q.x) * cos(q.y) + sin(q.y) * cos(q.z) + sin(q.z) * cos(q.x);
    return abs(g) / w - 0.5 * thickness;
}
`,
  },
]

function describe(code: string): string {
  const lines: string[] = []
  for (const line of code.replace(/\r\n?/g, '\n').split('\n')) {
    const m = line.match(/^\s*\/\/\s?(.*)$/)
    if (!m) break
    lines.push(m[1].trim())
  }
  return lines.join(' ')
}

function title(name: string): string {
  return name.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase()).replace(/\bTpms\b/, 'TPMS')
}

const fromFiles: FunctionExample[] = Object.entries(files)
  .map(([path, raw]) => {
    const name = path.split('/').pop()!.replace(/\.glsl$/, '')
    const box = BOXES[name] ?? { half: [20, 20, 20] as Vec3, lipschitz: 1.5 }
    return { name, title: title(name), description: describe(raw), code: raw.replace(/\r\n?/g, '\n'), ...box }
  })
  .sort((a, b) => (ORDER.indexOf(a.name) + 1 || 99) - (ORDER.indexOf(b.name) + 1 || 99) || a.name.localeCompare(b.name))

export const EXAMPLES: FunctionExample[] = [...STARTERS, ...fromFiles]

export function findExample(name: string): FunctionExample | undefined {
  const n = name.trim().toLowerCase().replace(/\.glsl$/, '')
  return EXAMPLES.find((e) => e.name === n)
}

export function exampleCreateArgs(ex: FunctionExample, center: Vec3 = [0, 0, ex.half[1]]) {
  return {
    name: ex.name,
    center,
    size: ex.half.map((h) => 2 * h) as Vec3,
    x_axis: [1, 0, 0],
    y_axis: [0, 0, 1],
    lipschitz: ex.lipschitz,
    code: ex.code,
  }
}
