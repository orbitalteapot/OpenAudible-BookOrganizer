import { Feather, Gauge, ShieldCheck, Zap } from 'lucide-react';
import Disclosure from '../ui/Disclosure';
import { Field } from '../ui/Field';
import SegmentedControl from '../ui/SegmentedControl';
import { Card } from '../ui/Surface';

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

const COPY_SPEEDS = [
  { value: 'normal', label: 'Normal', icon: Zap, description: 'Copies several books at once.' },
  {
    value: 'gentle',
    label: 'Gentle',
    icon: Feather,
    description: 'One book at a time. Use for network drives and USB disks.',
  },
];

// Changing an option while a sort runs is fine: the run keeps the options it started with.
const RUNNING_NOTE = 'Changes apply from the next sort.';

function OptionField({ label, field, options, settings, update, saving, error, runActive }) {
  const selected = options.find((option) => option.value === settings[field]) ?? options[0];
  const hint = (
    <>
      <span className="block">{selected.description}</span>
      {runActive && <span className="block">{RUNNING_NOTE}</span>}
      {error && <span className="block text-critical">{error}</span>}
    </>
  );

  return (
    <Field label={label} hint={hint} group>
      {(_, hintId) => (
        <SegmentedControl
          label={label}
          value={selected.value}
          options={options}
          describedBy={hintId}
          // The value only moves when the backend replies, so an arrow pressed before then would
          // count from the old value and could choose the option just left.
          disabled={saving}
          onChange={(value) => update({ [field]: value })}
        />
      )}
    </Field>
  );
}

/** How a sort checks and copies books, and how it lays them out. */
export default function OptionsCard({ settings, update, saving, fieldErrors, runActive }) {
  return (
    <Card title="Options">
      <div className="space-y-4">
        <OptionField
          label="Update check"
          field="comparisonMode"
          options={COMPARISON_MODES}
          settings={settings}
          update={update}
          saving={saving}
          error={fieldErrors.comparisonMode}
          runActive={runActive}
        />
        <OptionField
          label="Copy speed"
          field="copySpeed"
          options={COPY_SPEEDS}
          settings={settings}
          update={update}
          saving={saving}
          error={fieldErrors.copySpeed}
          runActive={runActive}
        />

        <Disclosure summary="How books are organised">
          <div className="space-y-2 text-2xs text-fg-subtle">
            <p>Every book gets its own folder in the destination:</p>
            <pre className="overflow-x-auto rounded border border-line bg-raised px-3 py-2 font-mono text-2xs text-fg-muted">
              {[
                'Author/',
                '  Series/',
                '    Book 1/',
                '      Title.m4b',
                '  Title/',
                '    Title.m4b',
              ].join('\n')}
            </pre>
            <p>
              Books in a numbered series go in Author / Series / Book N; other books in Author / Title. A hidden
              .openaudible-organizer file in the destination records which book is in which folder, so each book keeps
              its folder, and moves to a new one with its files when its author, series, number or title changes. Files left loose, or
              in a Book N folder two books shared, by older versions of this app are moved into their books&apos;
              folders. When several books share a loose file&apos;s name, or a sort has already been through the folder,
              it is moved only into the one with the same audio. Any loose audio file not moved is left where it is and
              listed under Problems.
            </p>
          </div>
        </Disclosure>
      </div>
    </Card>
  );
}
