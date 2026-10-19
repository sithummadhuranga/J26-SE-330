import type { Paged } from '../core/paging';

const arrow = (d: string) => (
  <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    <path d={d} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

/** The footer under a table: which rows are shown, and arrows to move between pages (bottom right). */
export function Pagination({ paged, noun }: { paged: Paged<unknown>; noun: string }) {
  const { page, pageCount, total, from, to, setPage } = paged;
  const first = page === 0;
  const last = page >= pageCount - 1;
  return (
    <nav className="pager" aria-label={`${noun} pages`}>
      <span className="pager__range" aria-live="polite">
        {total === 0 ? `No ${noun}` : `${from}–${to} of ${total}`}
      </span>
      <span className="pager__buttons">
        <button type="button" className="pager__btn" aria-label="First page" disabled={first} onClick={() => setPage(0)}>
          {arrow('M17.5 6l-6 6 6 6M11.5 6l-6 6 6 6')}
        </button>
        <button
          type="button"
          className="pager__btn"
          aria-label="Previous page"
          disabled={first}
          onClick={() => setPage(page - 1)}
        >
          {arrow('M14.5 6l-6 6 6 6')}
        </button>
        <span className="pager__page">
          Page {page + 1} of {pageCount}
        </span>
        <button type="button" className="pager__btn" aria-label="Next page" disabled={last} onClick={() => setPage(page + 1)}>
          {arrow('M9.5 6l6 6-6 6')}
        </button>
        <button
          type="button"
          className="pager__btn"
          aria-label="Last page"
          disabled={last}
          onClick={() => setPage(pageCount - 1)}
        >
          {arrow('M6.5 6l6 6-6 6M12.5 6l6 6-6 6')}
        </button>
      </span>
    </nav>
  );
}
