# Logs, métricas y trazas no son tres pilares. Son una sola petición.

# Logs, métricas y trazas no son tres pilares. Son una sola petición.

Aurora Coffee Co. tiene un checkout que llama a un servicio de precios y a uno de inventario. Le apunté un generador de carga — 1.000 órdenes, nada del otro mundo — y medí lo que de verdad esperó quien llamaba:

```json
{
  "requests": 1000,
  "mean_ms": 23.9,
  "p50_ms": 4.2,
  "p95_ms": 8.7,
  "p99_ms": 856.4,
  "max_ms": 1758.7
}
```

La orden mediana tardó 4,2 milisegundos. Una tardó 1,76 segundos, cuatrocientas veces más, y el promedio de 23,9 ms no describe ninguna de las dos. No es la petición típica ni la mala. Es un número que no le corresponde a nada de lo que ocurrió.

Durante toda esa corrida los tres servicios escribieron sus logs con toda normalidad. Nada falló, nada reintentó, ninguna alerta se disparó. La única persona que sabía que algo andaba mal era el cliente viendo girar un spinner, y esa persona no abre un ticket: se va.

Este artículo trata de los tres tipos de telemetría que responden la pregunta que nadie podía responder en esa corrida — *cuáles* órdenes estuvieron lentas y por qué — y de por qué "los tres pilares de la observabilidad" es una mala forma de pensarlos. No son tres sistemas puestos uno al lado del otro. Son tres proyecciones de los mismos eventos, y lo que los vuelve útiles no es tenerlos los tres: es el identificador que te permite saltar de uno a otro.



![Un histograma de latencia: una barra muy alta a la izquierda, luego una cola larga y baja, con el promedio marcado en el hueco vacío entre las dos, donde casi no cae ninguna petición](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/ucqi6dqn1skc01djoh5i.jpg)

## El monitoreo responde preguntas que ya escribiste

La distinción que importa no es "logs contra métricas". Es esta:

**El monitoreo responde preguntas que pensaste de antemano.** ¿La CPU pasó del 80%? ¿La tasa de errores pasó del 1%? ¿Se está llenando el disco? Cada una de esas preguntas alguien la escribió, la convirtió en un chequeo y le colgó una alerta. Eso funciona y deberías tenerlo.

**La observabilidad es poder responder preguntas que no pensaste de antemano**, sin desplegar código nuevo para averiguarlo. "¿Por qué exactamente veinte peticiones estuvieron lentas esta tarde y qué tenían en común?" no es una pregunta para la que alguien haya escrito un chequeo. Si responderla exige agregar una línea de log y volver a desplegar, el sistema no es observable: es depurable, en algún momento, con permiso.

Esa es toda la medida. No "¿tenemos un dashboard?", sino "cuando pase algo raro, ¿podemos averiguar qué fue con datos que ya recolectamos?".

---

## El sistema de prueba

Tres servicios pequeños en Node 24, para que cada número de este artículo sea reproducible y no un recuerdo. Checkout llama a precios y después a inventario:

```json
{
  "name": "aurora-observability",
  "private": true,
  "type": "module",
  "engines": { "node": ">=24" },
  "dependencies": {
    "@opentelemetry/api": "^1.9.1",
    "@opentelemetry/auto-instrumentations-node": "^0.80.0",
    "@opentelemetry/exporter-prometheus": "^0.222.0",
    "@opentelemetry/exporter-trace-otlp-http": "^0.222.0",
    "@opentelemetry/sdk-node": "^0.222.0",
    "pino": "^10.3.1"
  }
}
```

Detrás hay tres contenedores de código abierto. Sin cuenta, sin API key y sin capa gratuita que se venza:

```yaml
# docker-compose.yml
services:
  jaeger:
    image: jaegertracing/jaeger:2.21.0
    container_name: aurora-jaeger
    ports:
      - "16686:16686"   # la interfaz de trazas que vas a abrir
      - "4318:4318"     # OTLP sobre HTTP — a donde los servicios envían los spans

  prometheus:
    image: prom/prometheus:v3.15.0
    container_name: aurora-prometheus
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
    extra_hosts:
      # Prometheus consulta. Los servicios corren en el host, no en esta red.
      - "host.docker.internal:host-gateway"

  grafana:
    image: grafana/grafana:13.2.2
    container_name: aurora-grafana
    ports:
      - "3030:3000"
    environment:
      GF_AUTH_ANONYMOUS_ENABLED: "true"
      GF_AUTH_ANONYMOUS_ORG_ROLE: Admin
    depends_on:
      - prometheus
      - jaeger
```

```yaml
# prometheus.yml
global:
  scrape_interval: 5s

scrape_configs:
  - job_name: aurora
    static_configs:
      - targets:
          - host.docker.internal:9464   # checkout
          - host.docker.internal:9465   # precios
          - host.docker.internal:9466   # inventario
```

```bash
docker compose up -d
```

Un solo archivo arma toda la telemetría, y se carga con `--import` para que corra antes que cualquier código de la aplicación:

```ts
// telemetry.ts
import { register } from 'node:module';

// La instrumentación automática reescribe los módulos mientras se importan, y en ESM
// eso necesita un hook del cargador. Sin esta línea el SDK arranca igual y exporta
// igual: simplemente nunca ve node:http y nada queda correlacionado. Vuelvo a esto
// más abajo.
register('@opentelemetry/instrumentation/hook.mjs', import.meta.url);

import { NodeSDK } from '@opentelemetry/sdk-node';
import { getNodeAutoInstrumentations } from '@opentelemetry/auto-instrumentations-node';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { PrometheusExporter } from '@opentelemetry/exporter-prometheus';

const sdk = new NodeSDK({
  serviceName: process.env.OTEL_SERVICE_NAME ?? 'unnamed-service',
  // Las trazas se envían a Jaeger por OTLP.
  traceExporter: new OTLPTraceExporter({
    url: `${process.env.OTEL_EXPORTER_OTLP_ENDPOINT ?? 'http://localhost:4318'}/v1/traces`,
  }),
  // Las métricas se consultan: esto abre /metrics para que Prometheus lo lea.
  metricReader: new PrometheusExporter({ port: Number(process.env.METRICS_PORT ?? 9464) }),
  instrumentations: [getNodeAutoInstrumentations()],
});

sdk.start();

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.once(signal, () => {
    // Vacía lo que quedó en el lote pendiente. Si no, los últimos spans antes de un
    // despliegue — que suelen ser los interesantes — nunca salen del proceso.
    void sdk.shutdown().finally(() => process.exit(0));
  });
}
```

```bash
OTEL_SERVICE_NAME=pricing   METRICS_PORT=9465 node --import ./telemetry.ts pricing.ts
OTEL_SERVICE_NAME=inventory METRICS_PORT=9466 node --import ./telemetry.ts inventory.ts
OTEL_SERVICE_NAME=checkout  METRICS_PORT=9464 node --import ./telemetry.ts checkout.ts
```

Node 24 ejecuta esos archivos TypeScript directamente, y por eso aquí no hay paso de compilación ni `tsx`. Fíjate en el `.ts` explícito de `--import ./telemetry.ts`: Node resuelve el archivo real, no reescribe el especificador por ti.

La parte interesante del servicio de precios es que las promociones se evalúan por SKU y se guardan en caché, y los SKU de temporada tienen doscientas veces más reglas de promoción que las mezclas de la casa:

```ts
// pricing.ts (la parte que importa)
const cache = new Map<string, number>();

function ruleCountFor(sku: string): number {
  return sku.startsWith('seasonal-') ? 2400 : 12;
}

tracer.startActiveSpan('pricing.quote', (span) => {
  span.setAttribute('aurora.sku', sku);

  const cached = cache.get(sku);
  if (cached !== undefined) {
    span.setAttribute('aurora.cache_hit', true);
    span.end();
    respond(cached);
    return;
  }

  span.setAttribute('aurora.cache_hit', false);

  const priced = tracer.startActiveSpan('pricing.evaluate_rules', (inner) => {
    inner.setAttribute('aurora.rule_count', ruleCountFor(sku));
    const value = evaluateRules(sku);
    inner.end();
    return value;
  });

  cache.set(sku, priced);
  log.warn({ sku, rule_count: ruleCountFor(sku) }, 'priced on a cold cache');
  span.end();
  respond(priced);
});
```

Nadie escribió un bug. Hay una caché, funciona, y 977 de las 1.000 peticiones dieron con ella. Ese es justamente el punto: en producción las fallas interesantes casi nunca son errores de código, son *distribuciones*.

---

## Logs: qué pasó, contado por un solo proceso

Un log es un evento con marca de tiempo, emitido por un proceso, que describe algo que hizo. Es la señal más antigua y sigue siendo la más detallada: es el único lugar donde vive el *motivo*.

La primera mejora no tiene nada que ver con herramientas de observabilidad y además es gratis. Deja de redactar oraciones y empieza a emitir objetos:

```ts
// Antes: la información está, pero solo una persona puede extraerla
console.log(`precio de ${sku} con caché fría y ${rules} reglas`);

// Después: el mismo evento, ahora consultable
log.warn({ sku, rule_count: rules }, 'priced on a cold cache');
```

```json
{"level":40,"time":1790347139632,"service":"pricing","sku":"seasonal-19","rule_count":2400,"msg":"priced on a cold cache"}
```

La diferencia no es estética. A la primera versión solo puedes hacerle grep, así que "muéstrame los precios calculados con caché fría donde el número de reglas pasó de mil, agrupados por SKU" significa escribir una expresión regular contra una frase que cualquier compañero puede reescribir mañana. La segunda es un filtro sobre dos campos. Pino escribe esa forma por defecto; Serilog hace lo mismo en .NET.

Ahora, lo que los logs no pueden hacer, que es la razón de que existan las otras dos señales. Bajo carga, esos tres servicios producen líneas como estas, todas a la vez:

```json
{"time":1790347137929,"service":"checkout","sku":"house-blend-1kg","ms":3,"msg":"checkout complete"}
{"time":1790347137934,"service":"checkout","sku":"decaf-250g","ms":4,"msg":"checkout complete"}
{"time":1790347137938,"service":"checkout","sku":"espresso-500g","ms":3,"msg":"checkout complete"}
{"time":1790347139632,"service":"pricing","sku":"seasonal-19","rule_count":2400,"msg":"priced on a cold cache"}
{"time":1790347139695,"service":"checkout","sku":"seasonal-19","ms":1755,"msg":"checkout complete"}
```

Cada línea es cierta. Cada línea está estructurada. Y no hay forma *confiable* de afirmar que la línea de precios y la de checkout que sigue pertenecen a la misma orden. Parecen relacionadas porque el SKU coincide y las marcas de tiempo están a 63 ms — pero eso es una suposición, y con concurrencia real es una suposición equivocada. Diez clientes pidiendo la misma mezcla de temporada en el mismo segundo producen diez pares indistinguibles. Los logs describen eventos de un proceso a la vez; una petición que cruza tres procesos no tiene dueño en ese modelo.

Ahí está el hueco. No es falta de detalle — de eso los logs tienen de sobra. Es falta de **identidad**.



![Dos paneles: a la izquierda, tres servicios escribiendo tres montones de logs sin relación; a la derecha, esas mismas líneas sujetas a un solo hilo por una llave compartida](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/vvfj67t9tbdhh188n1o5.jpg)

---

## Trazas: una petición, en todos los procesos que tocó

Una **traza** (*trace*) es el recorrido de una petición. Está hecha de **spans**, y un span es una operación con nombre, duración y padre. El árbol que sale de ahí es lo que una traza *es*: no una lista de marcas de tiempo, sino una estructura causal — esta llamada ocurrió *a causa de* aquella, y dentro de su tiempo de vida.

Dos cosas hacen que eso funcione al cruzar la red. Cada span lleva el identificador de la traza, y el cliente HTTP inyecta ese identificador en una cabecera `traceparent` de salida, que el siguiente servicio vuelve a leer. Ese es todo el mecanismo del trazado distribuido, y por eso el identificador es lo que vale la pena cuidar.

Esta es la petición más lenta de la corrida de 1.000 órdenes, dibujada como árbol. Cada fila es el nombre, la duración y el servicio de un span; la columna de atributos está recortada a las dos URL de salida, que es de donde salen:

```text
trace 4eef76aeccdbd0f961d78dc7e4241092  (8 spans)

GET                            1756.1 ms  [checkout]
  checkout                     1755.6 ms  [checkout]
    GET                        1752.2 ms  [checkout]   -> localhost:3001/price
      GET                      1692.3 ms  [pricing]
        pricing.quote          1691.4 ms  [pricing]
          pricing.evaluate_rules  1690.7 ms  [pricing]
    GET                           1.6 ms  [checkout]   -> localhost:3002/reserve
      GET                         0.4 ms  [inventory]
```

Lee los números de arriba abajo. De 1.756 milisegundos, 1.690,7 se fueron dentro de `pricing.evaluate_rules`: el 96% de la petición en un solo span, cuatro niveles adentro, en un proceso distinto de aquel con el que hablaba el cliente. Inventario, que es el servicio del que todos sospechan primero porque toca una base de datos, respondió en cuatro décimas de milisegundo.

Nada de ese árbol está inferido. Cada span anidado está dentro de su padre porque llevaba encima el identificador del padre, no porque las marcas de tiempo se traslapen.

Y ahora la unión que los logs no podían hacer solos, porque el SDK agrega el identificador de la traza activa en cada línea escrita dentro de un span:

```json
{"level":40,"time":1790347139632,"service":"pricing","trace_id":"4eef76aeccdbd0f961d78dc7e4241092","span_id":"b56f30e5561dd16d","trace_flags":"01","sku":"seasonal-19","rule_count":2400,"msg":"priced on a cold cache"}
{"level":30,"time":1790347139695,"service":"checkout","trace_id":"4eef76aeccdbd0f961d78dc7e4241092","span_id":"2dafc950aac40ac6","trace_flags":"01","sku":"seasonal-19","ms":1755,"msg":"checkout complete"}
```

Son las mismas dos líneas de antes. Tres campos más, y "parecen relacionadas" se convierte en "son la misma petición". Deja ahí `trace_flags`: el `01` significa que esta traza se conservó y de verdad está en Jaeger. Una línea con `00` lleva un identificador que no conduce a ninguna parte, y la sección de muestreo del final es donde eso empieza a importar. Pega ese identificador en tu buscador de logs y obtienes todas las líneas que cualquier servicio escribió sobre esa orden; pégalo en Jaeger y obtienes el árbol de arriba. Ese es el puente, y es la razón para que te importe el trazado aunque nunca abras una interfaz de cascada.

Ese campo no lo escribes tú. `@opentelemetry/instrumentation-pino`, que `getNodeAutoInstrumentations()` activa por ti, agrega `trace_id`, `span_id` y `trace_flags` a toda línea de Pino emitida dentro de un span activo. El logger se queda en tres líneas:

```ts
// logger.ts
import pino from 'pino';

export const log = pino({
  level: process.env.LOG_LEVEL ?? 'info',
  base: { service: process.env.OTEL_SERVICE_NAME ?? 'unnamed-service' },
});
```



![Una cascada de ocho spans anidados entre tres servicios, donde el más profundo es casi tan largo como la petición completa y otros dos apenas se ven](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/cajwpynwrgqblfjg2ukb.jpg)

---

## Métricas: con qué frecuencia y qué tan grave

Una traza describe una petición. Esa es su fuerza y también todo su límite: no puedes mirar 1.000 trazas y formarte una opinión, y en producción ni siquiera vas a tenerlas todas.

Una **métrica** es un número agregado en el tiempo y agrupado por un conjunto pequeño de etiquetas. Checkout registra una:

```ts
const duration = meter.createHistogram('checkout.duration', {
  description: 'End-to-end checkout latency',
  unit: 'ms',
});

duration.record(elapsed, {
  route: '/checkout',
  outcome: reservation.reserved ? 'ok' : 'rejected',
});
```

Un histograma no guarda tus mediciones. Guarda *cuántas cayeron en cada bucket*, y por eso es lo bastante barato como para conservarlo para siempre. Esta es la lectura real después de la corrida, recortada: cada línea lleva además `otel_scope_name="aurora.checkout"`, y faltan los buckets `le="0"`, `50`, `75`, `250`, `5000`, `7500`, `10000` y `+Inf`:

```text
checkout_duration_count{route="/checkout",outcome="ok"} 1001
checkout_duration_sum{route="/checkout",outcome="ok"} 22441.000499999976
checkout_duration_bucket{route="/checkout",outcome="ok",le="5"} 910
checkout_duration_bucket{route="/checkout",outcome="ok",le="10"} 972
checkout_duration_bucket{route="/checkout",outcome="ok",le="25"} 976
checkout_duration_bucket{route="/checkout",outcome="ok",le="100"} 979
checkout_duration_bucket{route="/checkout",outcome="ok",le="500"} 981
checkout_duration_bucket{route="/checkout",outcome="ok",le="750"} 988
checkout_duration_bucket{route="/checkout",outcome="ok",le="1000"} 995
checkout_duration_bucket{route="/checkout",outcome="ok",le="2500"} 1001
```

Los buckets son acumulativos, así que léelos como una escalera. 910 de 1.001 peticiones terminaron en 5 ms o menos. 981 terminaron en menos de medio segundo, lo que significa que **20 peticiones, el 2% del tráfico, tardaron más que eso**, y 6 de ellas pasaron del segundo. (El conteo es 1.001 y no 1.000 porque checkout también midió la petición de calentamiento; esta es la visión que el servidor tiene de su propia latencia, no la del generador de carga.)

Ese dato es justo lo que ninguna traza te puede dar. Una traza demuestra que una petición estuvo lenta; el histograma dice cuánto de tu tráfico vive allá afuera, que es la diferencia entre "se quejó un cliente" y "una orden de cada cincuenta es inservible".

También deja ver por qué `sum / count` — 22.441 entre 1.001, o sea 22,4 ms — es el número menos útil de la página. Es el promedio de 910 peticiones de 5 ms y 20 peticiones de un segundo, y no describe a ninguno de los dos grupos. Alerta sobre el promedio y te vas a enterar de una caída más o menos cuando tus clientes dejen de tener el problema.

También funciona al revés. Pregúntale al histograma *cuáles* peticiones estuvieron lentas y no tiene nada: ahí no hay SKU, ni identificador de orden, ni cliente. Eso es a propósito, y la siguiente sección trata de lo que pasa cuando intentas arreglarlo.

---

## Las tres juntas: ese 2% que importa

Este es el ciclo, y el orden no es decorativo.

**Las métricas te dicen que hay un problema y qué tan grande es.** El 2% de los checkouts por encima de 500 ms. Ese número salió de datos recolectados antes de que alguien estuviera mirando, y es lo bastante barato como para guardarlo un año, así que también puedes ver que empezó el martes.

**Las trazas te dicen a dónde se fue el tiempo.** Abre una lenta y el 96% está dentro de `pricing.evaluate_rules`. Ya sabes el servicio, la función, y que la red y el inventario son inocentes, sin haber leído una línea de código.

**Los logs te dicen por qué.** La línea marcada con ese mismo identificador de traza dice `rule_count: 2400` y `priced on a cold cache`. Esa es la causa, en el vocabulario de la propia aplicación: este SKU tiene 2.400 reglas de promoción y su entrada en caché no estaba.

Métricas sin trazas te deja un número sobre el que no puedes actuar. Trazas sin métricas te deja una anécdota. Cualquiera de las dos sin logs te deja una ubicación sin explicación. Y las tres sin un identificador compartido te dejan tres dashboards y una reunión.

Los atributos del span cierran el caso. `pricing.quote` lleva `aurora.cache_hit`, así que "¿esto solo pasa con la caché fría?" es un filtro sobre spans y no una hipótesis. En toda la corrida, incluida la petición de calentamiento: 24 fallos de caché contra 977 aciertos, y todas las peticiones lentas fueron fallos.

---

## Tres tropiezos que me costaron una tarde cada uno

### 1. En ESM el SDK arranca bien y no correlaciona nada

Borra la línea `register(...)` de `telemetry.ts` y todo sigue arrancando. Sin error, sin advertencia, y los spans siguen llegando a Jaeger. Misma carga, mismo código, esto es lo que sale:

```text
con el hook del cargador           sin él
------------------------------------------------------------------
checkout   1 identificador         checkout   su propio identificador
pricing    el mismo                pricing    uno distinto
inventory  el mismo                inventory  ningún span
8 spans en un solo árbol           dos fragmentos sin relación
trace_id en cada línea de log      ninguna línea con trace_id
```

La instrumentación automática funciona reescribiendo módulos cuando se cargan. En CommonJS engancha `require`; en ESM necesita un hook del cargador registrado antes del primer `import` de cualquier cosa instrumentada. Sin eso `node:http` nunca queda parcheado, el lado servidor nunca lee la cabecera `traceparent` entrante, cada servicio empieza su propia traza y la instrumentación de Pino tampoco se engancha. Es exactamente el estado de "tres flujos de logs desconectados" del inicio de este artículo, solo que ahora además estás pagando por guardar spans.

Los spans manuales siguen funcionando, y eso es lo que lo vuelve tan silencioso. Solo lo vas a notar si miras dos servicios y encuentras dos identificadores de traza distintos para una misma petición.

### 2. Un campo es gratis en un span y carísimo en una métrica

`aurora.sku` como atributo de span no cuesta nada: un span es un evento, lleva sus propios campos, y las dos docenas de SKU de la corrida fueron dos docenas de valores repartidos en sus propios spans.

Pon ese mismo campo en el histograma y deja de ser un campo. Cada combinación distinta de etiquetas es una **serie temporal aparte**, y un histograma expone dieciocho líneas por serie. Medido sobre el exportador real:

```text
etiquetas                                    series esperadas   series reales
------------------------------------------------------------------------------
route + outcome                                          6        6  (108 líneas)
route + outcome + customer_id (1.000 ids)            6.000    2.000  (36.000 líneas)
```

La lectura pasó de un par de kilobytes a 4,4 MB. Pero mira la columna de la derecha, porque el daño real es lo que nadie espera: **el SDK limita cada métrica a 2.000 conjuntos de atributos y mete en silencio todo lo que pase de ahí en una sola serie.**

```text
checkout_duration_risky_count{otel_metric_overflow="true"} 4001
```

4.001 de 6.000 registros cayeron ahí. La métrica no se volvió cara: se volvió *incorrecta*, y la única señal es una etiqueta que la mayoría nunca ha visto.

La regla que te mantiene fuera de problemas: **una etiqueta de métrica debe tener un conjunto pequeño y acotado de valores que podrías escribir hoy en una hoja.** Ruta, estado, región, resultado. Nunca un identificador de usuario, ni uno de orden, ni un SKU de un catálogo abierto, ni una URL con un id adentro. El contexto de alta cardinalidad va en los spans y en las líneas de log, que es precisamente para lo que sirven.



![Spans sueltos con varias etiquetas cada uno, junto a una métrica cuyas series se multiplican en una cuadrícula densa hasta que un muro las limita y el excedente cae en un depósito de desborde](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/32t553vpzj8ktzbl8fnh.jpg)

### 3. Las trazas son la señal cara, y el muestreo no es opcional

Esas 1.000 peticiones produjeron siete spans cuando la caché acertó y ocho cuando falló: 7.032 en total. Sumando lo que recibió cada endpoint:

| endpoint | envíos | bytes |
|---|---:|---:|
| `/v1/traces` | 19 | 6.252.907 |
| `/v1/logs` | 28 | 166.783 |

No hay fila de `/v1/metrics` porque nunca se le envió nada. Prometheus consulta, así que las métricas no viajan por este camino — que es la línea más barata de cualquier presupuesto de telemetría.

Seis megabytes y cuarto de spans por mil checkouts. Son 889 bytes por span, unos 6,2 KB por petición, y treinta y siete veces el volumen de logs del mismo tráfico. Multiplica eso por el tráfico de producción y trazar cada petición deja de ser un valor por defecto y pasa a ser una línea del presupuesto.

La respuesta es el muestreo, y la versión que conviene saber ahora es: muestrea *trazas completas*, nunca spans sueltos, o vas a terminar con árboles incompletos. El SDK por defecto se queda con todo, y dos variables de entorno cambian eso sin tocar código:

```bash
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=0.1
```

Al abrir 2.000 spans con la configuración por defecto se conservaron 2.000; con esas dos variables se conservaron 201. El prefijo `parentbased_` es lo que hace que funcione: la decisión se toma una sola vez en el primer servicio que recibe la petición, viaja en `traceparent`, y todos los servicios siguientes la respetan. Así obtienes el 10% de las trazas enteras, y no el 10% de los spans repartidos entre todas.

Las métricas se quedan al 100%, que es la otra mitad de por qué las quieres: tu p99 sigue siendo exacto aunque hayas perdido el 90% de las trazas.

**Y aquí es donde esto se pone incómodo, porque el muestreo de cabecera choca con el ciclo que defendí en todo el artículo.** La decisión se toma cuando entra la petición, antes de que nadie sepa si va a ser lenta. De los 20 checkouts por encima de 500 ms de esta corrida, un muestreo al 10% conserva unos 2 — y si la cola fuera más rara, digamos una petición de cada mil, el histograma te seguiría diciendo que el problema existe mientras en Jaeger no hay ninguna traza que abrir. Las métricas dicen *con qué frecuencia*, las trazas dicen *dónde*, y el muestreo echa los dados justo sobre las peticiones que fuiste a buscar.

Peor todavía: el identificador de traza existe se haya conservado la traza o no, así que una línea de log puede llevar un identificador que no conduce a nada. Para eso está `trace_flags`, y por eso se queda en el ejemplo de arriba: `01` se conservó, `00` no, y diez minutos buscando un árbol que nunca se envió son un error fácil de evitar.

La solución de verdad es decidir *después* de que la traza esté completa, no antes de que empiece: muestreo de cola, que implica un OpenTelemetry Collector entre tus servicios y Jaeger, conservando el 100% de las trazas que pasan de cierta latencia y el 100% de las que tienen error, más un porcentaje del resto. No sale gratis: el Collector tiene que guardar cada traza en memoria hasta decidir, y todos los spans de una misma traza tienen que llegar a la misma instancia, que es un problema de enrutamiento aparte. Eso da para un artículo propio y llega en esta serie. Mientras tanto, ten claro que un porcentaje fijo en la cabecera es un control de costo, no una respuesta a "encuéntrame la lenta".

---

## Cuándo necesitas las tres y cuándo no

**Empieza con logs estructurados y un histograma de latencia por punto de entrada.** Un servicio solo con una base de datos ya obtiene con eso casi todo el valor. Una traza de una petición que nunca sale del proceso es un flame graph con pasos de más, y para eso un profiler funciona mejor.

**Agrega trazado en cuanto una petición cruce un límite de proceso.** Dos servicios y una cola ya bastan. Ese es el punto donde "cuál de los dos está lento" deja de poder responderse desde los logs, y no hay disciplina de logging que lo arregle: lo que falta es el identificador, no el detalle.

**Agrega métricas antes de creer que las necesitas**, porque son la única señal lo bastante barata como para conservarla un año con toda su fidelidad. No puedes volver atrás a preguntar cuál era el p99 del trimestre pasado si nadie estaba contando.

**Déjalo en paz cuando** el sistema es un cron, una CLI, o cualquier cosa donde la ejecución es la unidad de trabajo y la salida estándar ya cuenta la historia completa. Déjalo en paz también cuando no hay nadie de guardia: telemetría que nadie lee es un gasto con dashboard. Instrumentar es la mitad fácil; la difícil es que alguien tenga como trabajo mirarla.

**Regla práctica: necesitas una traza cuando la respuesta a "¿a dónde se fue el tiempo?" vive en un proceso distinto del que atiende la petición.**

---

## Puntos clave

- **El monitoreo responde preguntas que escribiste de antemano; la observabilidad responde las que no.** Si averiguarlo implica agregar una línea de log y volver a desplegar, tienes logging, no observabilidad.
- **Las tres señales son una misma petición vista de tres formas.** Las métricas dicen con qué frecuencia y qué tan grave, las trazas dicen dónde, los logs dicen por qué. El identificador de traza es lo que las vuelve un solo sistema en vez de tres pestañas, y en ESM basta con perder una línea `register(...)` para quedarte sin él en silencio.
- **El promedio es el número menos informativo que vas a calcular.** Una corrida con una mediana de 4,2 ms y un p99 de 856 ms promedia 23,9 ms, que no describe ninguna petición que haya ocurrido de verdad.
- **La cardinalidad es gratis en los spans y ruinosa en las métricas.** El SDK de OpenTelemetry limita cada métrica a 2.000 conjuntos de atributos y agrupa todo lo demás dentro de `otel_metric_overflow`, así que una etiqueta `customer_id` no solo cuesta dinero: deja la métrica incorrecta.
- **Las trazas son la señal cara.** Aquí 6,2 KB por petición, treinta y siete veces los logs. Muestrea trazas completas y deja las métricas al 100%: tus percentiles siguen exactos y tu factura no.

---

**Lo que sigue en esta línea:** logging estructurado en la práctica — cuánto cuestan de verdad Pino y Serilog bajo carga, y por qué "loguea todo como JSON y ya" se queda corto antes de lo que te gustaría.
