export type Language = 'en' | 'es';

export type Strings = {
	open: string;
	close: string;
	title: string;
	subtitle: string;
	placeholder: string;
	inputLabel: string;
	send: string;
	thinking: string;
	slow: string;
	sources: string;
	showPassage: string;
	source: (n: number) => string;
	talkToPerson: string;
	handoffTitle: string;
	handoffIntro: string;
	email: string;
	name: string;
	nameOptional: string;
	message: string;
	submit: string;
	cancel: string;
	sending: string;
	handoffDone: string;
	errors: {
		unavailable: string;
		network: string;
		rateLimited: (seconds: number | null) => string;
		originNotAllowed: string;
		invalidEmail: string;
		invalidName: string;
		messageRequired: string;
		messageTooLong: string;
		generic: string;
	};
	poweredBy: string;
};

const en: Strings = {
	open: 'Open the assistant',
	close: 'Close the assistant',
	title: 'Assistant',
	subtitle: 'Answers from our documents, with sources.',
	placeholder: 'Ask a question…',
	inputLabel: 'Your question',
	send: 'Send',
	thinking: 'Looking in the documents…',
	slow: 'Still working: the first answer can take a few seconds.',
	sources: 'Sources',
	showPassage: 'Show the passage',
	source: (n) => `Source ${n}`,
	talkToPerson: 'Talk to a person',
	handoffTitle: 'Talk to a person',
	handoffIntro: 'Leave your email and message; the team will reply by email.',
	email: 'Email',
	name: 'Name',
	nameOptional: '(optional)',
	message: 'Message',
	submit: 'Send to the team',
	cancel: 'Cancel',
	sending: 'Sending…',
	handoffDone: 'Thanks! Your message reached the team; they will reply to your email.',
	errors: {
		unavailable: 'The assistant is temporarily unavailable. You can leave a message for the team instead.',
		network: 'Could not reach the assistant. Check your connection and try again.',
		rateLimited: (s) => (s ? `Too many questions in a short time. Try again in ${s} seconds.` : 'Too many questions in a short time. Try again in a moment.'),
		originNotAllowed: 'This assistant is not enabled for this site.',
		invalidEmail: 'Enter a valid email address, like ana@example.com.',
		invalidName: 'The name must be a single line of at most 100 characters.',
		messageRequired: 'Write a message for the team.',
		messageTooLong: 'The message can be at most 2000 characters.',
		generic: 'Something went wrong. Please try again.',
	},
	poweredBy: 'Answers can be incomplete; check the sources.',
};

const es: Strings = {
	open: 'Abrir el asistente',
	close: 'Cerrar el asistente',
	title: 'Asistente',
	subtitle: 'Respuestas de nuestros documentos, con fuentes.',
	placeholder: 'Escribe tu pregunta…',
	inputLabel: 'Tu pregunta',
	send: 'Enviar',
	thinking: 'Buscando en los documentos…',
	slow: 'Sigo trabajando: la primera respuesta puede tardar unos segundos.',
	sources: 'Fuentes',
	showPassage: 'Ver el fragmento',
	source: (n) => `Fuente ${n}`,
	talkToPerson: 'Hablar con una persona',
	handoffTitle: 'Hablar con una persona',
	handoffIntro: 'Déjanos tu email y tu mensaje; el equipo te responderá por correo.',
	email: 'Email',
	name: 'Nombre',
	nameOptional: '(opcional)',
	message: 'Mensaje',
	submit: 'Enviar al equipo',
	cancel: 'Cancelar',
	sending: 'Enviando…',
	handoffDone: '¡Gracias! Tu mensaje llegó al equipo; te responderán a tu email.',
	errors: {
		unavailable: 'El asistente no está disponible en este momento. Puedes dejarle un mensaje al equipo.',
		network: 'No se pudo conectar con el asistente. Revisa tu conexión e inténtalo de nuevo.',
		rateLimited: (s) => (s ? `Demasiadas preguntas seguidas. Inténtalo de nuevo en ${s} segundos.` : 'Demasiadas preguntas seguidas. Inténtalo de nuevo en un momento.'),
		originNotAllowed: 'Este asistente no está habilitado para este sitio.',
		invalidEmail: 'Escribe un email válido, por ejemplo ana@ejemplo.com.',
		invalidName: 'El nombre debe ser una sola línea de hasta 100 caracteres.',
		messageRequired: 'Escribe un mensaje para el equipo.',
		messageTooLong: 'El mensaje puede tener como máximo 2000 caracteres.',
		generic: 'Algo salió mal. Inténtalo de nuevo.',
	},
	poweredBy: 'Las respuestas pueden estar incompletas; revisa las fuentes.',
};

/** The explicit data-lang wins; otherwise the page's language, then the browser's. English by default. */
export function pickLanguage(explicit: string | null | undefined): Language {
	const candidates = [explicit, document.documentElement.lang, navigator.language];
	for (const candidate of candidates) {
		const code = candidate?.trim().slice(0, 2).toLowerCase();
		if (code === 'es' || code === 'en') {
			return code;
		}
	}

	return 'en';
}

export const strings: Record<Language, Strings> = { en, es };
