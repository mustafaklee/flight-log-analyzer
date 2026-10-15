/** Series colors: distinguishable in light and dark themes (Mantine palette, shade 6). */
export const seriesColors = [
  'var(--mantine-color-blue-6)',
  'var(--mantine-color-orange-6)',
  'var(--mantine-color-teal-6)',
  'var(--mantine-color-grape-6)',
  'var(--mantine-color-red-6)',
  'var(--mantine-color-lime-7)',
  'var(--mantine-color-cyan-6)',
  'var(--mantine-color-pink-6)',
];

export const mantineColor = (name: string, shade = 6) => `var(--mantine-color-${name}-${shade})`;
