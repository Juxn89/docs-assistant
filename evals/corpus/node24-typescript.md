# Node 24 ejecuta TypeScript sin compilar: lo que funciona y lo que no

# Node 24 ejecuta TypeScript sin compilar: lo que funciona y lo que no

`node server.ts` funciona. Sin opciones extra, sin loaders, sin `tsx`, sin carpeta `dist/`. Node 24 lee el TypeScript, descarta los tipos y ejecuta lo que queda.

La opción que todavía aparece en todos los tutoriales, `--experimental-strip-types`, dejó de ser necesaria en Node 23.6. En la 24 puedes pasarla u omitirla y la salida es idéntica byte por byte. Yo la escribí una semana entera por pura inercia, como la contraseña de una cuenta que ya cerré.



![La misma línea de código dos veces: arriba los bloques de anotaciones de tipo, abajo huecos del mismo ancho, con las columnas todavía alineadas](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/ppaupxx9egt1rl0iwm50.jpg)

Eso de "descarta los tipos" es literal, y Node te lo muestra la primera vez que algo revienta. Este es un archivo completo: tres líneas de código, y una de ellas lleva dos anotaciones de tipo largas.

```ts
interface Invoice { id: string }

const describe = (invoice: Invoice, options: Readonly<Record<string, string>>): string => invoice.id.toUpperCase() + options.locale.trim();

console.log(describe({ id: "inv-1" }, {}));
```

Y esto es lo que imprimió Node al ejecutarlo:

```text
file:///C:/code/scratch/cols.ts:3
const describe = (invoice         , options                                  )         => invoice.id.toUpperCase() + options.locale.trim();
                                                                                                                                    ^

TypeError: Cannot read properties of undefined (reading 'trim')
    at describe (file:///C:/code/scratch/cols.ts:3:133)
```

Compara la línea 3 en los dos bloques. `: Invoice` son nueve caracteres, y en la copia de Node hay nueve espacios justo donde estaba. `: Readonly<Record<string, string>>` son treinta y cuatro, y el hueco que lo reemplazó también. Las dos versiones de esa línea miden exactamente 139 caracteres.

Ahí está el mecanismo completo: las anotaciones se sobrescriben en su lugar con la misma cantidad de espacios, nunca se eliminan. Por eso la columna 133 del archivo que Node ejecutó es la columna 133 del archivo que yo escribí, la flecha señala el carácter correcto, y no hizo falta ningún source map para ponerla ahí.

Esto es lo que aprendí al tomarme la idea en serio: primero en un servicio de cuatro módulos, después en los dos scripts que publican este blog. Funciona. También se niega a hacer cinco cosas concretas, y una de ellas falla de una forma que te va a costar una tarde si nadie te avisa antes.

## Qué está pasando en realidad

Node incluye Amaro, una capa delgada sobre el componente de swc que borra los tipos, compilado a WebAssembly. Cuando carga un archivo `.ts`, `.mts` o `.cts`, lo parsea, borra todo lo que existe solo para el verificador de tipos y le entrega el resultado a V8. Ese **borrado de tipos** (*type stripping*) es todo el mecanismo.

De esa única frase salen tres consecuencias, y cada problema de este artículo es una de ellas:

- **No lee tu `tsconfig.json`.** Ni `paths`, ni `experimentalDecorators`, ni `target`. Node ni siquiera busca el archivo.
- **No verifica tipos.** No valida nada. Ni resuelve nada.
- **No puede generar código.** Sobrescribir caracteres con espacios es el mecanismo completo, así que cualquier función de TypeScript que necesite *emitir* JavaScript queda fuera por diseño.

Puedes preguntarle al proceso en qué modo está corriendo:

```js
process.features.typescript
// "strip"      -> el valor por omisión en Node 24
// "transform"  -> con --experimental-transform-types
// false        -> con --no-strip-types
```

El último caso vale la pena conocerlo: `--no-strip-types` devuelve a `.ts` la condición de extensión desconocida, y es la forma de comprobar que un despliegue está ejecutando código ya compilado en lugar de borrar tipos en cada arranque.

## El ciclo sin compilación, en algo de verdad

Esta es la estructura que uso ahora para servicios pequeños: cuatro archivos, sin herramientas de build y sin nada instalado en tiempo de ejecución.

El `package.json` hace dos cosas interesantes: declara ESM y mapea `#src/*` para que los imports no se conviertan en cadenas de `../../..`.

```json
{
  "name": "invoice-api",
  "private": true,
  "type": "module",
  "imports": { "#src/*": "./src/*" },
  "scripts": {
    "dev": "node --watch server.ts",
    "test": "node --test",
    "check": "tsc --noEmit"
  }
}
```

`src/money.ts` es el módulo aburrido que existe para que el dinero nunca sea un número con decimales:

```ts
export type Cents = number;

/** Solo acepta una cantidad entera y no negativa de centavos: ni decimales, ni strings. */
export function parseCents(raw: unknown): Cents | null {
  return typeof raw === "number" && Number.isInteger(raw) && raw >= 0 ? raw : null;
}

export function formatCents(cents: Cents): string {
  return (cents / 100).toFixed(2);
}
```

`src/invoices.ts` tiene el dominio. Fíjate en el especificador del import: `#src/money.ts`, con la extensión del archivo que existe realmente en disco. Guarda ese detalle, porque más adelante vuelve convertido en un mensaje de error.

```ts
import { type Cents, parseCents } from "#src/money.ts";

export type InvoiceState = "draft" | "sent" | "paid";

export interface Invoice {
  readonly id: string;
  readonly customer: string;
  readonly amountCents: Cents;
  readonly state: InvoiceState;
}

export interface NewInvoice {
  readonly customer: string;
  readonly amountCents: Cents;
}

export function parseNewInvoice(body: unknown): NewInvoice | null {
  if (typeof body !== "object" || body === null) return null;

  const { customer, amountCents } = body as Record<string, unknown>;
  const cents = parseCents(amountCents);

  if (typeof customer !== "string" || customer.trim().length === 0) return null;
  if (cents === null) return null;

  return { customer: customer.trim(), amountCents: cents };
}

export class InvoiceStore {
  readonly #byId = new Map<string, Invoice>();
  #sequence = 0;

  add(draft: NewInvoice): Invoice {
    const invoice: Invoice = {
      id: `INV-${(++this.#sequence).toString().padStart(4, "0")}`,
      customer: draft.customer,
      amountCents: draft.amountCents,
      state: "draft",
    };

    this.#byId.set(invoice.id, invoice);
    return invoice;
  }

  find(id: string): Invoice | undefined {
    return this.#byId.get(id);
  }

  all(): readonly Invoice[] {
    return [...this.#byId.values()];
  }
}
```

`server.ts` usa `node:http`, sin framework, con las dos cosas que un ejemplo suele omitir y que en producción no se perdonan: un límite estricto al tamaño del cuerpo de la petición y un cierre que deja terminar las peticiones en curso.

```ts
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { InvoiceStore, parseNewInvoice, type Invoice } from "#src/invoices.ts";
import { formatCents } from "#src/money.ts";

const MAX_BODY_BYTES = 16 * 1024;
const store = new InvoiceStore();

function send(response: ServerResponse, status: number, payload: unknown): void {
  const body = JSON.stringify(payload);
  response.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),
  });
  response.end(body);
}

/** Lee el cuerpo con un límite fijo, para que un solo cliente no haga crecer el proceso sin control. */
async function readJson(request: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  let size = 0;

  for await (const chunk of request) {
    size += chunk.length;
    if (size > MAX_BODY_BYTES) throw new RangeError("cuerpo demasiado grande");
    chunks.push(chunk as Buffer);
  }

  return JSON.parse(Buffer.concat(chunks).toString("utf-8"));
}

function toResponseBody(invoice: Invoice): Record<string, unknown> {
  return { ...invoice, amount: formatCents(invoice.amountCents) };
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "localhost"}`);

  if (request.method === "GET" && url.pathname === "/invoices") {
    send(response, 200, store.all().map(toResponseBody));
    return;
  }

  if (request.method === "POST" && url.pathname === "/invoices") {
    let body: unknown;
    try {
      body = await readJson(request);
    } catch (error) {
      const status = error instanceof RangeError ? 413 : 400;
      send(response, status, { error: "cuerpo ilegible" });
      return;
    }

    const draft = parseNewInvoice(body);
    if (draft === null) {
      send(response, 422, { error: "customer debe ser un string no vacío y amountCents un número entero" });
      return;
    }

    send(response, 201, toResponseBody(store.add(draft)));
    return;
  }

  send(response, 404, { error: "esa ruta no existe" });
});

server.listen(Number(process.env["PORT"] ?? 3000), () => {
  console.log(`invoice-api escuchando en ${JSON.stringify(server.address())}`);
});

// Deja de aceptar conexiones y permite que terminen las peticiones en curso antes de salir.
for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.once(signal, () => {
    server.close(() => process.exit(0));
    server.closeIdleConnections();
  });
}
```

Con `node --watch server.ts` tienes el ciclo de recarga. Y la parte que más me gustó descubrir: el runner de pruebas integrado no necesita configuración de ningún tipo para ejecutar pruebas en TypeScript.

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { InvoiceStore, parseNewInvoice, type Invoice } from "#src/invoices.ts";

test("rechaza un monto que no sean centavos enteros", () => {
  assert.equal(parseNewInvoice({ customer: "Cafe Luna", amountCents: 1299.5 }), null);
});

test("rechaza un cliente vacío", () => {
  assert.equal(parseNewInvoice({ customer: "   ", amountCents: 1299 }), null);
});

test("numera las facturas desde uno y las crea como borrador", () => {
  const store = new InvoiceStore();
  const draft = parseNewInvoice({ customer: "Cafe Luna", amountCents: 129900 });
  assert.ok(draft);

  const invoice: Invoice = store.add(draft);
  assert.equal(invoice.id, "INV-0001");
  assert.equal(invoice.state, "draft");
});
```

`node --test` encuentra los `*.test.ts`, les borra los tipos y los ejecuta. Tres pruebas en 259 ms, arranque del proceso incluido. Sin configuración de Jest, sin `ts-jest`, sin ese bloque `transform` que nadie en el equipo entiende del todo.



![Cuatro archivos fuente que alimentan al runtime, del que salen watch, pruebas y ejecución, sin herramienta de build en medio](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/juclylh7veq72uqbwozp.jpg)

## No verifica tipos. En absoluto.

Este archivo se ejecuta sin queja alguna:

```ts
const amountCents: number = "129900";
console.log(amountCents.toFixed(2));
```

```text
TypeError: amountCents.toFixed is not a function
```

Un `TypeError` en ejecución por un error que el compilador habría marcado mientras yo lo estaba escribiendo. El borrado de tipos no es una implementación de TypeScript: es una manera eficiente de *ignorarlo*. La verificación sigue siendo tu responsabilidad, solo que deja de estar en el camino crítico entre guardar y ejecutar:

```bash
npx tsc --noEmit    # 838 ms en este proyecto, con el compilador nativo de TypeScript 7
```

Esa separación es la verdadera ganancia, y es más grande de lo que parece. La verificación ocurre en tu editor mientras escribes, y una vez más en CI. La ejecución ocurre cientos de veces al día, y ahora no espera a ningún compilador.

Dos opciones del `tsconfig.json` hacen que el verificador exija lo que Node realmente puede ejecutar:

```json
{
  "compilerOptions": {
    "module": "nodenext",
    "target": "es2024",
    "strict": true,
    "noEmit": true,
    "erasableSyntaxOnly": true,
    "verbatimModuleSyntax": true,
    "allowImportingTsExtensions": true,
    "types": ["node"]
  },
  "include": ["server.ts", "src/**/*.ts"]
}
```

`erasableSyntaxOnly` rechaza, en tiempo de verificación, cada construcción que Node va a rechazar en tiempo de ejecución: te da un `error TS1294` en lugar de una caída. `verbatimModuleSyntax` evita el único fallo de este artículo en el que de verdad perdí tiempo. Activa las dos *antes* de migrar algo, no después.

## Las cinco cosas que se rompen

### 1. Enums, namespaces y propiedades de constructor

No son anotaciones. Se compilan a código que existe en ejecución, y sobrescribir caracteres con espacios no puede producir código. Las tres fallan igual:

```text
SyntaxError [ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX]: TypeScript enum is not supported in strip-only mode
SyntaxError [ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX]: TypeScript namespace declaration is not supported in strip-only mode
SyntaxError [ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX]: TypeScript parameter property is not supported in strip-only mode
```

Ese mensaje está bien hecho: nombra la construcción y el modo. Las propiedades declaradas en el constructor (*parameter properties*) son las que más duelen, porque `constructor(private readonly baseUrl: string) {}` es el estilo sobre el que está construida media comunidad de NestJS.

`--experimental-transform-types` hace que las tres funcionen, con un costo: sigue siendo experimental, imprime una advertencia en cada ejecución y ya no conserva las posiciones, porque ahora Node genera código en lugar de borrar caracteres. Mi comprobación fue accidental: un archivo con un decorador en la línea 6 reportó el error de sintaxis en la línea 6 en el modo de solo borrado, y en la línea 5 con la transformación activada. Los stack traces sí salen correctos, porque en ese modo Node activa los source maps; un error de sintaxis ocurre antes de que exista un mapa.

La otra opción es dejar de escribir esas tres construcciones. Un tipo unión reemplaza casi cualquier enum y además el estrechamiento de tipos funciona mejor:

```ts
export type InvoiceState = "draft" | "sent" | "paid";
```

### 2. Decoradores, sin ninguna explicación

Los decoradores son la excepción a la regla del mensaje útil, y la razón es precisa: no son sintaxis exclusiva de TypeScript. Son una propuesta de JavaScript. El borrado de tipos los deja intactos, se los pasa a V8, y V8 todavía no los implementa:

```text
@logged
^

SyntaxError: Invalid or unexpected token
```

Ni `ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX`, ni una mención de TypeScript, ni una pista. Si vienes de NestJS o TypeORM, aquí se termina por ahora la idea de trabajar sin compilar, y `--experimental-transform-types` tampoco rescata los decoradores estándar. Usa `tsx` o un paso de compilación, y vuelve a revisarlo en un año.



![Anotaciones de tipo y genéricos pasando por un hueco en un muro, mientras enums, namespaces, propiedades de constructor, decoradores y un paquete instalado quedan detenidos](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/zw940e2u5t5hvk7op7yu.jpg)

### 3. El import que TypeScript jura que está bien

Este es el que me costó tiempo real:

```ts
import { Invoice, invoiceTotal } from "./types.ts";
```

```text
import { Invoice, invoiceTotal } from "./types.ts";
         ^^^^^^^
SyntaxError: The requested module './types.ts' does not provide an export named 'Invoice'
```

`Invoice` es una interface. `tsc` lo sabe y la elimina del import que emite, y por eso esta línea compila sin problemas y ha funcionado en todos los proyectos con paso de compilación que has escrito. Node no tiene ese conocimiento. Ve un import nombrado, V8 le pide ese nombre al módulo, el módulo no lo tiene, y recibes un error de módulos apuntando a una línea que tu verificador aprobó.

La solución es una palabra, y cuando ya la sabes la escribes en automático:

```ts
import { type Invoice, invoiceTotal } from "./types.ts";
```

`verbatimModuleSyntax: true` convierte esto de sorpresa en ejecución a error de verificación, que es donde corresponde. Estaba en mi `tsconfig.json` antes de que terminara de depurarlo. Te lo cuento porque el mensaje habla de módulos y de exports, así que pasé veinte minutos revisando mi mapa de `imports` en lugar de mirar la palabra `type` que faltaba.

### 4. Los imports nombran el archivo en disco, no el que planeas emitir

```ts
import { toDisplay } from "./money.js";   // money.js no existe
```

```text
Error [ERR_MODULE_NOT_FOUND]: Cannot find module '.../esm/money.js'
```

Node resuelve los especificadores como siempre lo ha hecho, sin reescribir extensiones. Cada import tiene que terminar en `.ts`, que es exactamente lo contrario de como se ve un proyecto basado en `tsc`: ahí terminan en `.js` precisamente porque eso es lo que va a existir después de compilar.

Ese es el obstáculo mecánico más grande para migrar un proyecto existente, y TypeScript 5.7 agregó las opciones que lo resuelven: `allowImportingTsExtensions` te deja escribir `.ts` en el código fuente, y `rewriteRelativeImportExtensions` reescribe esos especificadores a `.js` al emitir. Puedes tener imports nativos de Node y seguir publicando código compilado; probé las dos rutas sobre el mismo proyecto para estar seguro.

### 5. Los alias de rutas no existen, y `node_modules` está fuera de alcance

Node nunca lee `tsconfig.json`, así que `paths` no hace nada:

```text
TypeError [ERR_PACKAGE_IMPORT_NOT_DEFINED]: Package import specifier "#billing/types.ts" is not defined
```

El reemplazo es el campo `imports` del `package.json`: los subpath imports propios de Node, que TypeScript también entiende. Por eso el ejemplo de arriba mapea `#src/*`. Un solo mecanismo de alias, respetado por el runtime y por el verificador, sin plugins en medio.

El otro límite es más firme. El borrado de tipos está deshabilitado dentro de `node_modules`:

```text
Error [ERR_UNSUPPORTED_NODE_MODULES_TYPE_STRIPPING]: Stripping types is currently unsupported
for files under node_modules, for ".../node_modules/@acme/billing/index.ts"
```

O sea: no publiques un paquete cuyo punto de entrada sea un archivo `.ts`. Las librerías se siguen compilando.

El matiz que importa en un monorepo es el opuesto de lo que sugiere el error. Un paquete de workspace **sí** funciona, porque `npm install` lo enlaza con un symlink y Node resuelve por la ruta real, fuera de `node_modules`. Armé un workspace de npm con dos paquetes y `"exports": "./src/index.ts"`, lo importé de un paquete al otro, y se ejecutó. La restricción es para copias instaladas, no para tus paquetes internos.

## Los números

Mejor tiempo de 25 ejecuciones, en una laptop con i7-1165G7, Windows 11 y Node 24.16. Medir solo el arranque en una laptop es ruidoso, así que la columna honesta es el mínimo; dejo la mediana al lado para que veas la dispersión.

| Qué se ejecuta | Mínimo | Mediana |
| --- | --- | --- |
| archivo `.js` de 20 bytes | 66 ms | 94 ms |
| el mismo archivo como `.ts` | 90 ms | 114 ms |
| 109 KB de `.js` ya emitido | 69 ms | 75 ms |
| los 229 KB de `.ts` de donde salió | 153 ms | 191 ms |
| servicio de cuatro módulos, compilado | 100 ms | 118 ms |
| servicio de cuatro módulos, con borrado de tipos | 130 ms | 176 ms |
| servicio de cuatro módulos, con `tsx` | 486 ms | 590 ms |

De esa tabla salen dos conclusiones.

**El costo fijo es de unos 25 ms**, que es instanciar en WebAssembly el componente que borra los tipos, algo que antes no cargabas. El costo variable ronda los 0.26 ms por kilobyte de TypeScript: 229 KB en un solo archivo cuestan 84 ms y un archivo de 20 bytes cuesta 24 ms. El costo sigue el tamaño del código fuente, no el del proyecto, así que un servicio de cuatro módulos pequeños paga casi nada.

**`tsx` arranca unas 3.7 veces más lento aquí, y sigue siendo la herramienta correcta para varios proyectos.** Esa diferencia te compra decoradores, enums, alias de rutas e interoperabilidad con CommonJS. Es un intercambio razonable; solo que ahora conviene hacerlo a propósito y no por costumbre.



![Tres barras de arranque: el código compilado la más corta, el borrado de tipos un poco más larga con su costo fijo rayado, y tsx unas cuatro veces la más corta](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/4ydrwma84n432yb0oykt.jpg)

## Lo que yo intenté hacer de verdad

Este blog son dos scripts de TypeScript independientes — uno publica en la API de dev.to, el otro postea en LinkedIn y X — y los dos corren con `tsx`. No importan nada más que módulos `node:`. Candidatos perfectos para eliminar una dependencia.

Los dos funcionaron con `node` pelado en el primer intento. Después busqué con grep cualquier otra mención de la herramienta y encontré esto, dentro del script de publicación:

```ts
spawnSync("tsx", [socialScript, "--url", result.url, "--file", filePath], {
  stdio: "inherit",
  shell: true,
});
```

El nombre del binario escrito a mano, en una rama del código que solo se ejecuta cuando pasas `--social`. Editar el `package.json` habría "eliminado" `tsx` un martes y roto la publicación el viernes siguiente, en un camino que ni el verificador de tipos ni una ejecución normal tocan nunca. La solución es lanzar el intérprete que ya está corriendo:

```ts
spawnSync(process.execPath, [socialScript, "--url", result.url, "--file", filePath], {
  stdio: "inherit",
});
```

`process.execPath` además vuelve innecesario el `shell: true`, que no es poco para un cambio de una línea. Y la lección sirve más allá de este repositorio: la dependencia que intentas eliminar casi nunca está solo en el `package.json`. Busca el nombre del binario antes de celebrar.

## Cuándo usar cada cosa

**`node` pelado**: scripts, herramientas de línea de comandos, pruebas, servicios pequeños, cualquier cosa que escribas desde cero sobre Node 24. Sin dependencias y con una pieza menos entre tu editor y el proceso.

**`tsx`**: decoradores, enums o propiedades de constructor que no vas a reescribir; alias de rutas que no puedes convertir; dependencias en CommonJS con interoperabilidad incómoda; un proyecto donde todos los imports terminan en `.js` y la migración no entra en este trimestre.

**Un paso de compilación de verdad**: publicar una librería en npm, donde la restricción de `node_modules` es absoluta; empaquetar o minificar para un destino de despliegue; apuntar a un Node anterior al 22; o un presupuesto de arranque en frío tan ajustado que no puedas gastar 25 ms de borrado de tipos más un milisegundo por cada cuatro kilobytes de código.

Y en los tres casos, conserva `tsc --noEmit`. El borrado de tipos no eliminó la necesidad de verificar tipos: sacó al compilador del camino entre guardar un archivo y verlo correr, que era la parte que te hacía esperar.

---

## Puntos clave

- **La opción ya no hace falta.** `node server.ts` corre en Node 24 sin opciones extra, `--experimental-strip-types` ya no hace nada, y `process.features.typescript` reporta `"strip"`, `"transform"` o `false`.
- **Los tipos se sobrescriben con espacios, no se eliminan.** Las posiciones en bytes se conservan, así que las líneas y columnas de un stack trace son exactas sin ningún source map: los espacios se ven en la línea que Node imprime.
- **Nada se verifica, así que `tsc --noEmit` se queda.** Agrega `erasableSyntaxOnly` y `verbatimModuleSyntax` para que el verificador rechace lo que Node va a rechazar, empezando por el `import { AlgunaInterface }` al que le falta la palabra `type`.
- **Se rompen cinco cosas:** enums, namespaces y propiedades de constructor (con un error claro), decoradores (con un `SyntaxError: Invalid or unexpected token` a secas), imports que nombran archivos `.js` inexistentes, los alias de rutas del `tsconfig`, y cualquier archivo `.ts` instalado dentro de `node_modules`.
- **Presupuesta unos 25 ms más 0.26 ms por KB de código** — cerca de 3.7 veces más rápido de arrancar que `tsx` sobre los mismos archivos, razón suficiente para usar `node` pelado por omisión y `tsx` a propósito.
