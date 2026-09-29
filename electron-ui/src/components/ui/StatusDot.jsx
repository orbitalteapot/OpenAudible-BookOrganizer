import { AlertCircle, CheckCircle2, CircleDashed } from 'lucide-react';

const TONES = {
  positive: { icon: CheckCircle2, cls: 'text-positive' },
  critical: { icon: AlertCircle, cls: 'text-critical' },
  neutral: { icon: CircleDashed, cls: 'text-fg-subtle' },
};

/** A short status with an icon, so it does not rely on colour: "Found", "Not set". */
export default function StatusDot({ tone = 'neutral', children }) {
  const { icon: Icon, cls } = TONES[tone] ?? TONES.neutral;

  return (
    <span className={`inline-flex items-start gap-1.5 ${cls}`}>
      <Icon size={13} className="mt-0.5 shrink-0" aria-hidden="true" />
      <span className="min-w-0 break-words">{children}</span>
    </span>
  );
}
