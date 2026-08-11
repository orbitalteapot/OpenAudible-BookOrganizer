import { memo } from 'react';
import {
  FileSpreadsheet,
  FolderOpen,
  FolderOutput,
  Gauge,
  Play,
  ShieldCheck,
  Square,
} from 'lucide-react';
import { useIsElectron, useSortRun } from '../hooks';
import Button from './ui/Button';
import { Field, PathInput } from './ui/Field';
import SegmentedControl from './ui/SegmentedControl';
import { Banner, Card, ProgressBar, Stat } from './ui/Surface';

const COMPARISON_MODES = [
  {
    value: 'quick',
    label: 'Quick',
    icon: Gauge,
    description: 'Replaces a book when its size or sampled contents differ. Fast enough to run every time.',
  },
  {
    value: 'full',
    label: 'Verify contents',
    icon: ShieldCheck,
    description: 'Compares every byte, so a re-release of identical size is still caught. Slower.',
  },
];

const PATHS = [
  { key: 'csvPath', label: 'OpenAudible CSV export', icon: FileSpreadsheet, picker: 'file' },
  { key: 'sourcePath', label: 'Source folder', icon: FolderOpen, picker: 'folder' },
  { key: 'destPath', label: 'Destination folder', icon: FolderOutput, picker: 'folder' },
];

function SortPanel({ config, setConfig, run, setRun }) {
  const isElectron = useIsElectron();
  const { start, cancel } = useSortRun({ run, setRun, config });

  const { sorting, progress, error } = run;
  const comparisonMode = config.comparisonMode === 'full' ? 'full' : 'quick';
  const selectedMode = COMPARISON_MODES.find((mode) => mode.value === comparisonMode);

  const browse = async (key, picker) => {
    const path =
      picker === 'file'
        ? await window.electronAPI?.openFile([{ name: 'CSV Files', extensions: ['csv'] }])
        : await window.electronAPI?.openFolder();

    if (path) setConfig((prev) => ({ ...prev, [key]: path }));
  };

  const ready = config.csvPath && config.sourcePath && config.destPath;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div>
        <h1 className="text-lg font-semibold text-fg">Sort</h1>
        <p className="text-xs text-fg-muted">Organize your files into Author / Series / Book folders</p>
      </div>

      <div className="grid min-h-0 flex-1 auto-rows-min grid-cols-1 items-start gap-4 overflow-y-auto xl:grid-cols-2">
        <Card title="Configuration">
          <div className="space-y-4">
            {PATHS.map(({ key, label, icon, picker }) => (
              <Field key={key} label={label}>
                {(id) => (
                  <div className="flex gap-2">
                    <PathInput
                      id={id}
                      icon={icon}
                      value={config[key]}
                      placeholder={isElectron ? 'Not selected' : 'Configured by the server'}
                    />
                    {isElectron && (
                      <Button onClick={() => browse(key, picker)} disabled={sorting}>
                        Browse
                      </Button>
                    )}
                  </div>
                )}
              </Field>
            ))}

            <Field label="Update check" hint={selectedMode?.description} group>
              {() => (
                <SegmentedControl
                  label="Update check"
                  name="comparison-mode"
                  value={comparisonMode}
                  options={COMPARISON_MODES}
                  disabled={sorting}
                  onChange={(value) => setConfig((prev) => ({ ...prev, comparisonMode: value }))}
                />
              )}
            </Field>
          </div>

          {error && <Banner tone="critical" className="mt-4">{error}</Banner>}

          <div className="mt-5 flex gap-2">
            <Button variant="primary" icon={Play} className="flex-1" loading={sorting} disabled={!ready} onClick={start}>
              {sorting ? 'Sorting' : 'Start sorting'}
            </Button>
            {sorting && (
              <Button icon={Square} onClick={cancel}>
                Cancel
              </Button>
            )}
          </div>
        </Card>

        <ProgressCard sorting={sorting} progress={progress} />
      </div>
    </div>
  );
}

function ProgressCard({ sorting, progress }) {
  if (!sorting && !progress) {
    return (
      <Card title="Progress">
        <p className="py-8 text-center text-sm text-fg-subtle">
          Progress will appear here once a sort starts.
        </p>
      </Card>
    );
  }

  const canceled = progress?.isCanceled;
  const failed = progress?.error;
  const complete = progress?.isComplete && !failed && !canceled;

  const copied = progress?.copiedBooks || 0;
  const updated = progress?.updatedBooks || 0;
  const failedCount = progress?.failedBooks || 0;
  const skipped =
    progress?.skippedBooks ?? Math.max(0, (progress?.currentBook || 0) - copied);
  const warnings = progress?.warningCount || 0;

  const status = failed ? 'Failed' : canceled ? 'Canceled' : complete ? 'Complete' : 'Sorting';
  const tone = failed ? 'critical' : canceled ? 'caution' : complete ? 'positive' : 'accent';

  return (
    <Card title="Progress">
      <div className="space-y-5">
        <div>
          <div className="mb-2 flex items-baseline justify-between">
            <span className="text-sm text-fg-muted">{status}</span>
            <span className="tabular text-sm font-medium text-fg">
              {(progress?.percentage || 0).toFixed(0)}%
            </span>
          </div>
          <ProgressBar value={progress?.percentage} tone={tone} label={`Sort progress: ${status}`} />
          {progress?.totalBooks > 0 && (
            <p className="tabular mt-2 text-2xs text-fg-subtle">
              {(progress.currentBook || 0).toLocaleString()} of {progress.totalBooks.toLocaleString()} books
            </p>
          )}
        </div>

        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          <Stat label="Copied" value={copied} tone="positive" />
          <Stat label="Updated" value={updated} tone="accent" />
          <Stat label="Skipped" value={skipped} tone="caution" />
          <Stat label="Failed" value={failedCount} tone="critical" />
        </div>

        {progress?.currentTitle && !progress?.isComplete && (
          <CurrentBook label={progress.currentTitle} />
        )}

        {complete && (
          <Banner tone="positive">
            {copied.toLocaleString()} book{copied === 1 ? '' : 's'} copied
            {updated > 0 && `, ${updated.toLocaleString()} updated in place`}
            {skipped > 0 && `, ${skipped.toLocaleString()} already up to date`}
            {failedCount > 0 && `, ${failedCount.toLocaleString()} failed`}.
          </Banner>
        )}

        {canceled && (
          <Banner tone="caution">
            Canceled after {copied.toLocaleString()} book{copied === 1 ? '' : 's'}. Files already copied are
            complete; re-running picks up where this left off.
          </Banner>
        )}

        {failed && <Banner tone="critical">{progress.error}</Banner>}

        {warnings > 0 && (
          <p className="text-2xs text-fg-subtle">
            {warnings.toLocaleString()} warning{warnings === 1 ? '' : 's'} recorded — see the backend log.
          </p>
        )}
      </div>
    </Card>
  );
}

/** The backend sends "Artist: X | Series: Y | Title: Z"; show it as fields rather than a run-on line. */
function CurrentBook({ label }) {
  const details = label
    .split('|')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => {
      const separator = part.indexOf(':');
      return separator === -1
        ? { label: null, value: part }
        : { label: part.slice(0, separator).trim(), value: part.slice(separator + 1).trim() };
    });

  return (
    <div className="rounded border border-line bg-raised px-3.5 py-3">
      <p className="mb-1.5 text-2xs text-fg-subtle">Current book</p>
      <dl className="space-y-1">
        {details.map(({ label: key, value }, index) => (
          <div key={key ?? index} className="flex gap-2 text-sm">
            {key && <dt className="w-14 shrink-0 text-fg-subtle">{key}</dt>}
            <dd className="min-w-0 break-words text-fg-muted">{value}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

export default memo(SortPanel);
