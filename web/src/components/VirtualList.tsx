import { Box, ScrollArea } from '@mantine/core';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useRef, type ReactNode } from 'react';

/** Renders only the visible rows of a long list (events, parameters, signals). */
export function VirtualList<T>({
  items,
  rowHeight,
  height,
  renderRow,
  label,
}: {
  items: T[];
  rowHeight: number;
  height: number | string;
  renderRow: (item: T, index: number) => ReactNode;
  label: string;
}) {
  const viewport = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns functions the compiler cannot memoize.
  const virtualizer = useVirtualizer({ count: items.length, getScrollElement: () => viewport.current, estimateSize: () => rowHeight, overscan: 12 });

  return (
    <ScrollArea h={height} viewportRef={viewport} type="auto">
      <Box role="list" aria-label={label} pos="relative" h={virtualizer.getTotalSize()}>
        {virtualizer.getVirtualItems().map((row) => (
          <Box
            role="listitem"
            key={row.key}
            pos="absolute"
            top={0}
            left={0}
            w="100%"
            h={row.size}
            style={{ transform: `translateY(${row.start}px)` }}
          >
            {renderRow(items[row.index]!, row.index)}
          </Box>
        ))}
      </Box>
    </ScrollArea>
  );
}
