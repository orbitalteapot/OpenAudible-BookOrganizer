import { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { counts, idleStatus, runningStatus, scheduleResponse, settingsResponse } from '../../test/fixtures';
import ScheduleCard from '../sort/ScheduleCard';

/**
 * The card with its hooks stood in for: `changeInterval` succeeds and the backend then reports the
 * schedule as `afterChange` says.
 */
function Harness({ initial, settings = settingsResponse(), isElectron = false, runStatus = idleStatus(), afterChange }) {
  const [schedule, setSchedule] = useState(initial);
  const changeInterval = async (minutes) => {
    setSchedule(afterChange ? afterChange(minutes) : { ...schedule, intervalMinutes: minutes });
    return true;
  };

  return (
    <ScheduleCard
      scheduleState={{ schedule, error: null, saving: false, changeInterval }}
      settingsState={{ settings, update: vi.fn(), saving: false }}
      runStatus={runStatus}
      isElectron={isElectron}
    />
  );
}

const radio = (name) => screen.getByRole('radio', { name });

describe('ScheduleCard', () => {
  it('shows a schedule the server sets, read-only, even when it is not a preset', () => {
    render(<Harness initial={scheduleResponse({ intervalMinutes: 120, locked: true, nextRunUtc: new Date(Date.now() + 3_600_000).toISOString() })} />);

    expect(screen.getByText("Every 2 hours. Set by the server's SORT_INTERVAL setting.")).toBeTruthy();
    expect(radio('Every 2 hours').getAttribute('aria-checked')).toBe('true');
    expect(radio('Daily').getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByText(/in 1 hour/)).toBeTruthy();
  });

  it('says why a schedule that is on cannot run', () => {
    render(
      <Harness initial={scheduleResponse({ intervalMinutes: 1440, blockedReason: 'CSV_PATH is not set.', nextRunUtc: new Date().toISOString() })} />
    );

    expect(screen.getByText("Automatic sorting can't run: CSV_PATH is not set.")).toBeTruthy();
  });

  it("shows the server's configuration warnings", () => {
    const warning = 'SORT_INTERVAL="6x" was ignored — use a value like 6h, 12h or 1d.';
    render(<Harness initial={scheduleResponse()} settings={settingsResponse({ serverWarnings: [warning] })} />);

    expect(screen.getByText(warning)).toBeTruthy();
  });

  it('cannot be turned on until the paths are set, and says what is missing', () => {
    render(<Harness initial={scheduleResponse()} settings={settingsResponse({ csvPath: null })} isElectron />);

    expect(radio('Daily').getAttribute('aria-disabled')).toBe('true');
    fireEvent.click(radio('Daily'));
    expect(radio('Off').getAttribute('aria-checked')).toBe('true');
    expect(screen.getByText('Choose the CSV export first.')).toBeTruthy();
  });

  it('says the first sort starts now when turned on, until that sort has run', async () => {
    const onSchedule = (minutes) =>
      scheduleResponse({ intervalMinutes: minutes, nextRunUtc: new Date().toISOString() });
    const { rerender } = render(<Harness initial={scheduleResponse()} afterChange={onSchedule} />);

    expect(screen.getByText('The first sort starts as soon as you turn this on.')).toBeTruthy();
    fireEvent.click(radio('Daily'));

    expect(await screen.findByText('The first sort starts now, then every day.')).toBeTruthy();

    // Once the backend picks the first sort up, the card follows it (the harness keeps its state).
    rerender(<Harness initial={scheduleResponse()} afterChange={onSchedule} runStatus={runningStatus({ trigger: 'scheduled' })} />);
    expect(screen.getByText('An automatic sort is running.')).toBeTruthy();
  });

  it('does not promise a first sort when turned on during a sort, which covers that slot', async () => {
    const onSchedule = (minutes) =>
      scheduleResponse({ intervalMinutes: minutes, nextRunUtc: new Date(Date.now() + 24 * 3_600_000).toISOString() });
    render(<Harness initial={scheduleResponse()} afterChange={onSchedule} runStatus={runningStatus()} />);

    fireEvent.click(radio('Daily'));

    expect(await screen.findByText(/^tomorrow at /)).toBeTruthy();
    expect(screen.queryByText(/The first sort starts now/)).toBeNull();
  });

  it('reports the last automatic run in the same words as the Progress card', () => {
    render(
      <Harness
        initial={scheduleResponse({
          intervalMinutes: 1440,
          nextRunUtc: new Date(Date.now() + 3 * 3_600_000).toISOString(),
          lastRun: {
            startedUtc: new Date().toISOString(),
            finishedUtc: new Date().toISOString(),
            trigger: 'scheduled',
            counts: counts({ new: 3, upToDate: 120 }),
            problemCount: 0,
            isCanceled: false,
            error: null,
            errorCode: null,
          },
        })}
      />
    );

    expect(screen.getByText(/^today at .* — Sort complete: 3 new, 120 up to date\.$/)).toBeTruthy();
    // "tomorrow" late in the evening.
    expect(screen.getByText(/^(today|tomorrow) at .* \(in 3 hours\)$/)).toBeTruthy();
  });

  it('says when a failed run is retried, and why it failed', () => {
    render(
      <Harness
        initial={scheduleResponse({
          intervalMinutes: 1440,
          retrying: true,
          nextRunUtc: new Date(Date.now() + 15 * 60_000).toISOString(),
          lastRun: {
            startedUtc: new Date().toISOString(),
            finishedUtc: new Date().toISOString(),
            trigger: 'scheduled',
            counts: counts(),
            problemCount: 0,
            isCanceled: false,
            error: 'The destination folder does not exist. Is the drive connected?',
            errorCode: 'destinationMissing',
          },
        })}
      />
    );

    expect(
      screen.getByText(/— last attempt failed: The destination folder does not exist\. Is the drive connected\?$/)
    ).toBeTruthy();
    expect(screen.queryByText('Last sort')).toBeNull();
  });

  it('lets the arrow keys look through the choices without turning automatic sorting on', async () => {
    const changes = [];
    render(
      <Harness
        initial={scheduleResponse()}
        afterChange={(minutes) => {
          changes.push(minutes);
          return scheduleResponse({ intervalMinutes: minutes, nextRunUtc: new Date().toISOString() });
        }}
      />
    );

    radio('Off').focus();
    fireEvent.keyDown(radio('Off'), { key: 'ArrowRight' });
    fireEvent.keyDown(radio('6 hours'), { key: 'ArrowRight' });

    expect(document.activeElement).toBe(radio('12 hours'));
    expect(radio('Off').getAttribute('aria-checked')).toBe('true');
    expect(changes).toEqual([]);

    // Space or Enter on a button is a click: that is the choice.
    fireEvent.click(document.activeElement);
    await waitFor(() => expect(changes).toEqual([720]));
  });

  it('keeps focus on a busy choice instead of disabling it', () => {
    render(
      <ScheduleCard
        scheduleState={{ schedule: scheduleResponse({ intervalMinutes: 1440 }), error: null, saving: true, changeInterval: vi.fn() }}
        settingsState={{ settings: settingsResponse(), update: vi.fn(), saving: true }}
        runStatus={idleStatus()}
        isElectron
      />
    );

    // A disabled button drops focus to the page; these only say they are busy.
    expect(radio('Daily').disabled).toBe(false);
    expect(radio('Daily').getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByRole('switch', { name: 'Start when I sign in' }).disabled).toBe(false);
  });

  it('offers the background switches on the desktop while a schedule is on', async () => {
    render(<Harness initial={scheduleResponse({ intervalMinutes: 1440 })} isElectron />);

    const keep = screen.getByRole('switch', { name: 'Keep running in the background when the window is closed' });
    expect(keep.getAttribute('aria-checked')).toBe('false');
    expect(screen.getByRole('switch', { name: 'Start when I sign in' })).toBeTruthy();
    expect(screen.getByText('Sorts only while the app is open; a missed sort runs when you open it.')).toBeTruthy();

    fireEvent.click(radio('Off'));
    await waitFor(() => expect(screen.queryByRole('switch')).toBeNull());
  });
});
