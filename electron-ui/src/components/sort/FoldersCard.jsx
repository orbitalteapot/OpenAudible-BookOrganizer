import { FileSpreadsheet, FolderOpen, FolderOutput } from 'lucide-react';
import { chooseCsvFile, chooseFolder } from '../../desktop';
import { PATH_FIELDS } from '../../paths';
import Button from '../ui/Button';
import { Field, PathInput } from '../ui/Field';
import StatusDot from '../ui/StatusDot';
import { Card } from '../ui/Surface';

const ICONS = { csvPath: FileSpreadsheet, sourcePath: FolderOpen, destinationPath: FolderOutput };

/**
 * One path's status line: the last error the backend gave for it, otherwise what the backend sees
 * there now. In the browser the paths come from the container, so that is where it says to look.
 */
function describePath({ kind, variable }, status, error, isElectron) {
  if (error) return { tone: 'critical', text: error };

  const where = isElectron ? '' : ' inside the container — check the volume mapping';
  switch (status) {
    case 'ok':
      return { tone: 'positive', text: 'Found' };
    case 'notFound':
      return { tone: 'critical', text: `${kind === 'file' ? 'File' : 'Folder'} not found${where}` };
    case 'notWritable':
      return { tone: 'critical', text: "Can't write to this folder" };
    default:
      return { tone: 'neutral', text: isElectron ? 'Not set' : `Not set — set ${variable} in the container` };
  }
}

/** Why a path cannot be changed right now, or null when it can. */
function browseBlockedReason({ variable }, locked, runActive) {
  if (locked) return `Set by the server's ${variable} setting.`;
  if (runActive) return "Folders can't be changed while a sort is running.";
  return null;
}

/**
 * The export and the two folders. A path picked here is saved straight away; when the backend
 * refuses it, the row keeps the path that is saved and shows why underneath.
 *
 * `errors` maps a field to its last error, from saving it or from a sort refused because of it.
 * `onPicked(field)` is told before a new path is saved, so an error about the old one can go.
 */
export default function FoldersCard({ settings, update, errors, runActive, isElectron, onPicked }) {
  const browse = async ({ field, kind }) => {
    const path = kind === 'file' ? await chooseCsvFile() : await chooseFolder();
    if (!path) return;
    onPicked(field);
    await update({ [field]: path });
  };

  return (
    <Card title="Folders">
      <div className="space-y-4">
        {PATH_FIELDS.map((path) => {
          const status = describePath(path, settings.pathStatus?.[path.status], errors[path.field], isElectron);
          const blocked = browseBlockedReason(path, settings.locks?.paths, runActive);

          return (
            <Field key={path.field} label={path.label} hint={<StatusDot tone={status.tone}>{status.text}</StatusDot>}>
              {(id, hintId) => (
                <div className="flex gap-2">
                  <PathInput
                    id={id}
                    icon={ICONS[path.field]}
                    value={settings[path.field]}
                    placeholder="Not set"
                    invalid={status.tone === 'critical'}
                    describedBy={hintId}
                  />
                  {isElectron && (
                    <Button disabledReason={blocked} onClick={() => browse(path)} aria-label={`Browse for the ${path.noun}`}>
                      Browse
                    </Button>
                  )}
                </div>
              )}
            </Field>
          );
        })}
      </div>
    </Card>
  );
}
