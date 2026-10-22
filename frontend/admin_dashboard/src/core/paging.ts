import { useState } from 'react';

/** Rows per page in every table. */
export const pageSize = 10;

export interface Paged<T> {
  /** The rows on the current page. */
  rows: T[];
  /** Counts from zero. It's always a page that exists, even if the list got shorter. */
  page: number;
  pageCount: number;
  total: number;
  /** Positions of the first and last rows shown, counting from one. Both are 0 when the list is empty. */
  from: number;
  to: number;
  setPage: (page: number) => void;
}

/** Splits an already-sorted list (newest first) into pages of [size]. */
export function usePaged<T>(items: readonly T[], size = pageSize): Paged<T> {
  const [page, setPage] = useState(0);
  const pageCount = Math.max(1, Math.ceil(items.length / size));
  const current = Math.min(page, pageCount - 1);
  const start = current * size;
  return {
    rows: items.slice(start, start + size),
    page: current,
    pageCount,
    total: items.length,
    from: items.length === 0 ? 0 : start + 1,
    to: Math.min(start + size, items.length),
    setPage: (p) => setPage(Math.max(0, Math.min(p, pageCount - 1))),
  };
}
