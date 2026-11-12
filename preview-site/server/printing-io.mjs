import fs from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { setTimeout as sleep } from 'node:timers/promises';

// Windows antivirus/spooler readers can briefly deny replacement of an open journal.
// Keep the old record intact and retry only the file replacement, never the print.
export async function writePrintJson(target, value, { fileSystem = fs, wait = sleep } = {}) {
  const temporary = target + '.' + randomUUID() + '.tmp';
  await fileSystem.writeFile(temporary, JSON.stringify(value), { flag: 'wx', mode: 0o600 });
  try {
    for (let attempt = 0; ; attempt++) {
      try { await fileSystem.rename(temporary, target); return; }
      catch (error) {
        if (!['EPERM', 'EACCES', 'EBUSY'].includes(error.code) || attempt >= 7) throw error;
        await wait(Math.min(20 * 2 ** attempt, 250));
      }
    }
  } catch (error) {
    await fileSystem.unlink(temporary).catch(() => {});
    throw error;
  }
}
