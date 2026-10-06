import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { runningStatus } from '../../test/fixtures';
import RunPill from '../RunPill';

describe('RunPill', () => {
  it('says a run is getting ready instead of showing a frozen 0%', () => {
    render(<RunPill status={runningStatus({ preparing: true, percentage: 0 })} onOpen={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Sorting… getting ready. Show progress' })).toBeTruthy();
    expect(screen.queryByText(/0%/)).toBeNull();
  });

  it('names an automatic sort and its progress', () => {
    render(<RunPill status={runningStatus({ trigger: 'scheduled', percentage: 42 })} onOpen={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Automatic sort running · 42%. Show progress' })).toBeTruthy();
  });
});
