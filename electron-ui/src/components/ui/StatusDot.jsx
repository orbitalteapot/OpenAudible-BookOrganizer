import { AlertCircle, CheckCircle2, CircleDashed } from 'lucide-react';

const TONES = {
  positive: { icon: CheckCircle2, cls: 'text-positive' },
  critical: { icon: AlertCircle, cls: 'text-critical' },
  neutral: { icon: CircleDashed, cls: 'text-fg-subtle' },
};

/**
 * A short status with an icon, so it does not rely on colour: "Found", "Not set". The icon sits in a
 * box one line tall, so it is centred on the first line at any text size and stays there when a long
 * message wraps.
 */
export default function StatusDot({ tone = 'neutral', children }) {
  const { icon: Icon, cls } = TONES[tone] ?? TONES.neutral;

  return (
    <span className={`inline-flex items-start gap-2 ${cls}`}>
      <span className="flex h-[1lh] shrink-0 items-center">
        <Icon size={13} aria-hidden="true" />
      </span>
      <span className="min-w-0 break-words">{children}</span>
    </span>
  );
}
