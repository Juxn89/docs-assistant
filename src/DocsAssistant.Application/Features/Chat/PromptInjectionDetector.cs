using System.Text.RegularExpressions;

namespace DocsAssistant.Application.Features.Chat;

/// <summary>
/// Recognises text that tries to act as instructions to the assistant rather than content: overriding
/// the rules, asking for the prompt, assigning a new role, faking the message structure, posing as the
/// system. English and Spanish. Used on questions and on retrieved passages alike.
///
/// Deliberately generic: the patterns describe the <em>shape</em> of an injection, not the words of any
/// particular attack. They were written before running the injection eval against them and are not
/// tuned to its marker words — a filter fitted to its own test set would score well and protect
/// nothing. What it misses is reported by the eval, not hidden.
///
/// Phrases a real document or customer uses innocently stay out: "system prompt" as a topic (articles
/// about AI), "you are now ready", "override the default rules", "¿cuáles son tus reglas de
/// devolución?", "sin restricciones". The first version flagged three paragraphs of the eval corpus
/// for discussing system prompts; the patterns now require the text to address the assistant.
///
/// It is one layer. Passages are also delimited and escaped, and the model is told they are data.
/// </summary>
public static partial class PromptInjectionDetector
{
	public static bool IsSuspicious(string? text) =>
		!string.IsNullOrWhiteSpace(text) && Patterns().IsMatch(text);

	[GeneratedRegex(
		"""
		# English — override the rules
		\b(ignore|disregard|forget)\b[^.\n]{0,30}\b(previous|prior|above|earlier|preceding|your|these|my)\b[^.\n]{0,20}\b(instructions?|rules|prompts?|directives|guidelines)\b
		| \b(ignore|disregard|forget)\s+(all|everything)\b[^.\n]{0,20}\b(instructions?|prompts?)\b
		| \b(ignore|disregard|forget)\b[^.\n]{0,30}\b(instructions?|rules)\s+(above|before)\b
		# English — ask for the hidden prompt
		| \b(what\s+(is|are)|reveal|print|show|repeat|output|copy|leak|recite|tell\s+me)\b[^.\n]{0,30}\byour\s+((system|hidden|initial|original)\s+)?(prompt|instructions)\b
		# English — reassign the role
		| \byou\s+are\s+now\s+(a|an)\s+(\w+\s+){0,3}(assistant|ai|bot|chatbot|model)\b
		| \bfrom\s+now\s+on,?\s+you\s+(are|will|must|should|answer|respond|reply)\b
		| \bpretend\s+(to\s+be|you\s+are)\b
		| \b(unrestricted|jailbroken|uncensored)\s+(assistant|mode|model|ai)\b
		| \b(assistant|model|ai)\s+with\s+no\s+(rules|restrictions)\b
		| \b(answer|respond|reply)\w*\s+without\s+(rules|restrictions)\b
		# English — pose as the operator
		| \bsystem\s+(notice|override)\b | \b(new|updated)\s+(rule|instruction)s?\s*:
		# Spanish — anular las reglas
		| \b(ignora|ignore|ignoren|olvida|olviden|desobedece)\b[^.\n]{0,30}\b(instrucciones|reglas|indicaciones)\s+(anteriores|previas|de\s+arriba|del\s+sistema)\b
		| \b(ignora|ignore|ignoren|olvida|olviden|desobedece)\b[^.\n]{0,30}\b(tus|todas\s+las|todas\s+tus)\s+(instrucciones|reglas|indicaciones)\b
		# Spanish — pedir el prompt
		| \b(cuáles\s+son|revela|muestra|muéstrame|copia|cópia\w*|repite|imprime|dime)\b[^.\n]{0,30}\btus\s+instrucciones\b
		| \b(revela|muestra|muéstrame|copia|repite|imprime|dime)\b[^.\n]{0,30}\btu\s+prompt\b
		# Spanish — cambiar el rol
		| \ba\s+partir\s+de\s+ahora\s+(eres|serás|vas\s+a\s+ser)\b
		| \b(asistente|modelo|ia)\s+sin\s+(reglas|restricciones)\b
		| \b(responde|contesta)\w*\s+sin\s+(reglas|restricciones)\b
		| \b(nueva|nuevas)\s+reglas?\s*: | \baviso\s+del\s+sistema\b
		# Either — fake the message structure the assistant uses
		| </?\s*(system|instructions?|question|documents?|assistant|user)\b[^>]{0,40}>
		""",
		RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
	private static partial Regex Patterns();
}
