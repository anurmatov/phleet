// Log lines for PiExecutor, which reads Pi's stderr (phleet #386 §12).
// One line per record, prefixed `INFO `, `WARN ` or `ERROR `. Callers pass counts
// and names only, never message content or configuration values.

export interface Logger {
	info(line: string): void;
	warn(line: string): void;
	error(line: string): void;
}

/** Collapse any line breaks so one call is always exactly one log line. */
export function oneLine(text: string): string {
	return text.replace(/[\r\n\u2028\u2029]+/g, " ").trim();
}

export function createLogger(write: (line: string) => void): Logger {
	return {
		info: (line) => write(`INFO ${oneLine(line)}\n`),
		warn: (line) => write(`WARN ${oneLine(line)}\n`),
		error: (line) => write(`ERROR ${oneLine(line)}\n`),
	};
}

export const stderrLogger: Logger = createLogger((line) => {
	process.stderr.write(line);
});

/** In-memory logger for tests. */
export function createMemoryLogger(): Logger & { lines: string[] } {
	const lines: string[] = [];
	return { lines, ...createLogger((line) => lines.push(line.replace(/\n$/, ""))) };
}
