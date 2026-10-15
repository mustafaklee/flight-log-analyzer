import { createTheme } from '@mantine/core';

export const theme = createTheme({
  primaryColor: 'blue',
  defaultRadius: 'sm',
  fontFamily: 'Inter, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
  fontFamilyMonospace: 'ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace',
  components: {
    Card: { defaultProps: { radius: 'md' } },
  },
});
