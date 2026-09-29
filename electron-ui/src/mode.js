/**
 * Which app the page is part of: the desktop app's window, or a browser tab on the Docker container.
 * The two differ in what a person can do about a problem, so messages that tell them what to do ask
 * here rather than guessing.
 */

/** The page is the desktop app's window: the preload script has handed it the app's API. */
export function isDesktop() {
  return typeof window !== 'undefined' && Boolean(window.electronAPI);
}

/** What to say when the organizer stops answering, in terms of what the person can actually do. */
export function unreachableMessage() {
  return isDesktop()
    ? 'The organizer stopped responding. Restart the app to continue.'
    : "Can't reach the organizer server. Check that the container is running, then reload this page.";
}
