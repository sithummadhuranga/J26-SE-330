import { useState } from 'react';

/** Rows per page in every table. */
export const pageSize = 10;

export interface Paged<T> {
  /** The rows on the current page. */
  rows: T[];
  /** Zero-based; always a page that exists, even after the list shrank. */
  page: number;
  pageCount: number;
  total: number;
  /** One-based positions of the first and last row shown; 0 and 0 for an empty list. */
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
