import { Banner } from './ui/Surface';

/**
 * What is true of the whole app rather than of one page, shown above every page: that the organizer
 * has stopped answering, without which a running sort just vanishes from the sidebar as if it had
 * finished; and what the server has to say about its own settings (they were reset, cannot be
 * saved, or values in its environment were ignored), which someone who opens the app on the Library
 * page has to see there, not at the bottom of a card on the Sort page.
 */
export default function AppNotices({ lostContact, serverWarnings = [] }) {
  if (!lostContact && serverWarnings.length === 0) return null;

  return (
    <div className="mb-4 space-y-2">
      {lostContact && <Banner tone="critical">{lostContact}</Banner>}
      {serverWarnings.map((warning) => (
        <Banner key={warning} tone="caution">
          {warning}
        </Banner>
      ))}
    </div>
  );
}
