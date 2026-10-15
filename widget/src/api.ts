/** The API contract, as documented in docs/errors.md. */

export type Citation = {
	number: number;
	title: string;
	source: string;
	section: string | null;
	excerpt: string;
};

export type Done = {
	conversationId: string;
	outcome: string;
	offerHandoff: boolean;
	citations: Citation[];
};

export type AnswerEvent =
	| { type: 'delta'; text: string }
	| { type: 'done'; done: Done }
	| { type: 'error'; code: string };

/** A failure before or instead of a stream, in the terms the UI needs to explain it. */
export type Failure =
	| { kind: 'rate_limited'; retryAfterSeconds: number | null }
	| { kind: 'problem'; code: string | null }
	| { kind: 'network' };

export class ApiFailure extends Error {
	constructor(readonly failure: Failure) {
		super(failure.kind);
	}
}

export type HandoffRequest = {
	conversationId: string | null;
	email: string;
	name: string | null;
	message: string;
};

export class Api {
	constructor(
		private readonly baseUrl: string,
		private readonly key: string,
	) {}

	/** Streams the answer. Throws ApiFailure when the request is refused or cannot be made. */
	async *ask(question: string, conversationId: string | null, signal: AbortSignal): AsyncGenerator<AnswerEvent> {
		const response = await this.post(`/v1/chat/${encodeURIComponent(this.key)}`, { question, conversationId }, signal);
		if (!response.body) {
			throw new ApiFailure({ kind: 'network' });
		}

		for await (const message of readServerSentEvents(response.body)) {
			const data: unknown = JSON.parse(message.data);
			if (message.event === 'delta' && isRecord(data) && typeof data['text'] === 'string') {
				yield { type: 'delta', text: data['text'] };
			} else if (message.event === 'done' && isRecord(data)) {
				yield { type: 'done', done: data as Done };
			} else if (message.event === 'error' && isRecord(data)) {
				yield { type: 'error', code: String(data['code'] ?? 'assistant.unavailable') };
			}
		}
	}

	async handoff(request: HandoffRequest): Promise<void> {
		await this.post(`/v1/handoff/${encodeURIComponent(this.key)}`, request);
	}

	private async post(path: string, body: unknown, signal?: AbortSignal): Promise<Response> {
		let response: Response;
		try {
			// No credentials: the API identifies the assistant by its public key, never by cookies.
			response = await fetch(this.baseUrl + path, {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify(body),
				credentials: 'omit',
				...(signal ? { signal } : {}),
			});
		} catch (error) {
			if (error instanceof DOMException && error.name === 'AbortError') {
				throw error;
			}

			throw new ApiFailure({ kind: 'network' });
		}

		if (response.ok) {
			return response;
		}

		if (response.status === 429) {
			const seconds = Number.parseInt(response.headers.get('retry-after') ?? '', 10);
			throw new ApiFailure({ kind: 'rate_limited', retryAfterSeconds: Number.isFinite(seconds) ? seconds : null });
		}

		const problem: unknown = await response.json().catch(() => null);
		throw new ApiFailure({ kind: 'problem', code: isRecord(problem) && typeof problem['code'] === 'string' ? problem['code'] : null });
	}
}

type ServerSentEvent = { event: string; data: string };

/**
 * Server-Sent Events over fetch: EventSource cannot POST. Handles events split across network
 * chunks, multi-line data fields and CRLF line endings.
 */
export async function* readServerSentEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<ServerSentEvent> {
	const reader = body.getReader();
	const decoder = new TextDecoder();
	let pending = '';
	let buffer = '';
	try {
		while (true) {
			const { value, done } = await reader.read();
			if (done) {
				break;
			}

			// stream: true keeps a multi-byte character split across network chunks intact. A trailing
			// "\r" waits for the next chunk: it may be the first half of "\r\n".
			pending += decoder.decode(value, { stream: true });
			const cut = pending.endsWith('\r') ? pending.length - 1 : pending.length;
			buffer += pending.slice(0, cut).replace(/\r\n?/g, '\n');
			pending = pending.slice(cut);
			let boundary = buffer.indexOf('\n\n');
			while (boundary >= 0) {
				const parsed = parseEvent(buffer.slice(0, boundary));
				buffer = buffer.slice(boundary + 2);
				if (parsed) {
					yield parsed;
				}

				boundary = buffer.indexOf('\n\n');
			}
		}

		const last = parseEvent(buffer + pending.replace(/\r/g, '\n'));
		if (last) {
			yield last;
		}
	} finally {
		reader.releaseLock();
	}
}

function parseEvent(block: string): ServerSentEvent | null {
	let event = 'message';
	const data: string[] = [];
	for (const line of block.split('\n')) {
		if (line.startsWith(':') || line.length === 0) {
			continue;
		}

		const colon = line.indexOf(':');
		const field = colon < 0 ? line : line.slice(0, colon);
		const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
		if (field === 'event') {
			event = value;
		} else if (field === 'data') {
			data.push(value);
		}
	}

	return data.length > 0 ? { event, data: data.join('\n') } : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === 'object' && value !== null;
}
