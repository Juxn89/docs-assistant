# RabbitMQ from Fundamentals to Production, Part 1: Exchanges, Queues, and Bindings

# RabbitMQ from Fundamentals to Production, Part 1: Exchanges, Queues, and Bindings

> **Correction, 12 October 2026.** The retry counter in this article originally read the `x-death` header. On RabbitMQ 4.x that header does not survive the consumer republishing a message, so the count never passed 1 and a failing order was retried forever. The consumer below now counts retries in a header of its own. The same measurements showed that `x-delivery-limit` does not count an explicit requeue, and the dead-letter section now says so. [Part 2](https://dev.to/jgomezdev/rabbitmq-from-fundamentals-to-production-part-2-building-a-scalable-service-4nk2) has the captures.

Aurora Coffee Co. takes an order, and four things have to happen: charge the card, reserve the beans, email a receipt, and record the sale for the analytics team. Wire that up as four HTTP calls inside the checkout handler and you have built a machine with four single points of failure. The analytics service redeploys, its socket refuses the connection, and a customer who successfully paid gets a 500.

The instinct is to make those calls fire-and-forget so checkout stops waiting on them. That fixes the latency and keeps the bug: nothing now remembers that the receipt was never sent. A message broker is the piece that remembers. This post is about the part of RabbitMQ everyone skips on the way to a working "hello world" — the routing model — because getting it wrong is what makes people conclude that queues are unpredictable.



![Four synchronous HTTP calls out of checkout, one of them broken and returning a 500](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/0ltbfk4anpg3iwznne9p.jpg)

## A queue is not the same thing as `async`

`async`/`await` and a background task move work off the request thread. They do not move it out of the process. Whatever you scheduled lives in your service's memory, and the moment that pod is rescheduled, the work is gone with no trace that it ever existed.

A broker changes the ownership of the work. The producer hands the message to RabbitMQ and gets an acknowledgement that RabbitMQ now owns it. From that point:

- **It survives your process.** Redeploy the consumer mid-batch and the unacknowledged messages go back on the queue.
- **It survives the broker restart**, if you asked for that — durable queues and persistent messages, two separate settings that people routinely confuse and which we will get to.
- **It can go to more than one place.** One "order placed" message, four independent consumers, none of which know the others exist.
- **It applies backpressure instead of falling over.** A spike that would have knocked over the analytics service becomes a queue that gets deeper and then drains.

That last one is the honest trade. A queue does not make slow work fast. It makes slow work *someone else's problem, later*, and gives you a number — queue depth — that tells you when "later" has stopped arriving.

---

## The model: broker, exchange, queue, binding

Here is the rule that the tutorials bury, and it is the whole article:

> **A producer never publishes to a queue. It publishes to an exchange.**

The producer names an **exchange** and attaches a **routing key** — a short string describing what happened. The exchange holds no messages. Its only job is to look at its **bindings** and decide which **queues** get a copy. Zero matching bindings means zero copies, and the message is discarded.

Four pieces, and each one has exactly one responsibility:

| Piece | Job | Knows about |
|---|---|---|
| **Exchange** | Receives published messages and routes them | Its own bindings |
| **Binding** | A rule connecting an exchange to a queue, usually with a binding key | One exchange, one queue |
| **Queue** | Stores messages until a consumer acknowledges them | Nothing upstream |
| **Consumer** | Reads from one queue and acknowledges | One queue |

The payoff is that **producers and consumers never learn each other's names**. Checkout publishes `order.us.placed` to the `orders` exchange. Whether that message is read by one consumer, four, or none at all is decided entirely by bindings, which you can add and remove at runtime without redeploying the producer. Adding the fraud-detection team's new consumer to an existing event stream is a `bindQueue` call, not a sprint.

### Routing key vs. binding key

Two names for two different things, and mixing them up is the single most common source of "why is my queue empty":

- The **routing key** is set by the **producer**, per message, at publish time.
- The **binding key** is set on the **binding**, once, when you wire a queue to an exchange.

The exchange compares one against the other. *How* it compares them is what the exchange type decides.



![A producer publishing to an exchange whose bindings route copies to two queues, while an unmatched routing key reaches none](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/0d2szsapf8fg8i0jzt52.jpg)

---

## Run RabbitMQ locally first

Everything below is runnable, so start the broker before reading further. One file, no cloud account, no managed-service signup:

```yaml
# docker-compose.yml
services:
  rabbitmq:
    image: rabbitmq:4-management
    container_name: aurora-rabbitmq
    ports:
      - "5672:5672"    # AMQP — what your app connects to
      - "15672:15672"  # management UI — what you connect to
    environment:
      RABBITMQ_DEFAULT_USER: aurora
      RABBITMQ_DEFAULT_PASS: aurora-dev
    volumes:
      - rabbitmq-data:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "check_running"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  rabbitmq-data:
```

```bash
docker compose up -d
```

The `-management` tag is the part worth insisting on. Open `http://localhost:15672` (aurora / aurora-dev) and you get a live view of every exchange, queue, and binding, plus the depth and rate of each queue. It is the rare admin panel you will actually be glad to see, because "is my binding wrong?" stops being a guess — the Exchanges tab shows you exactly which binding keys the exchange is holding.

The project itself is two dependencies:

```json
{
  "name": "aurora-orders-queue",
  "private": true,
  "type": "module",
  "engines": { "node": ">=24" },
  "dependencies": { "amqplib": "^0.10.5" },
  "devDependencies": { "@types/amqplib": "^0.10.6", "typescript": "^5.9.0" }
}
```

Node 24 runs TypeScript files directly, so every snippet here runs with `node src/publisher.ts` — no build step and no `tsx`. That is also why the imports below carry an explicit `.ts` extension: Node resolves the real file, it does not rewrite the specifier for you. (What that feature does and does not handle is a post of its own, coming later this month.)

---

## The three exchange types that matter

RabbitMQ ships four types. Three of them cover essentially everything you will build.

### `fanout` — everybody gets a copy

A fanout exchange ignores the routing key completely and copies each message to **every** bound queue. This is the "order placed, four teams care" case:

```ts
await channel.assertExchange('order.events', 'fanout', { durable: true });

await channel.assertQueue('inventory.reserve', { durable: true });
await channel.assertQueue('analytics.ingest', { durable: true });

// Fanout ignores the routing key, which means the binding key is ignored too.
// Pass an empty string rather than inventing a value that will mislead the next reader.
await channel.bindQueue('inventory.reserve', 'order.events', '');
await channel.bindQueue('analytics.ingest', 'order.events', '');

channel.publish('order.events', '', payload, { persistent: true });
```

Both queues now receive their own independent copy. `inventory.reserve` acknowledging a message has no effect whatsoever on the copy sitting in `analytics.ingest` — separate queues, separate lifetimes, separate failures. Fanout is the cheapest way to add a consumer to an event later without touching the producer.

### `direct` — exact string match

A direct exchange delivers to the queues whose binding key equals the routing key, character for character:

```ts
await channel.assertExchange('receipts', 'direct', { durable: true });

await channel.assertQueue('receipts.email', { durable: true });
await channel.assertQueue('receipts.sms', { durable: true });

await channel.bindQueue('receipts.email', 'receipts', 'email');
await channel.bindQueue('receipts.sms', 'receipts', 'sms');

// Lands in receipts.email only. receipts.sms never sees it.
channel.publish('receipts', 'email', payload, { persistent: true });
```

Worth knowing: nothing stops two queues from sharing a binding key on a direct exchange, and if they do, both get a copy. Direct means "exact match", not "exactly one destination".

### `topic` — pattern match, and the one you will actually use

A topic exchange treats the routing key as dot-separated words and matches binding keys containing two wildcards. `*` matches **exactly one** word; `#` matches **zero or more** words.

Adopt a routing-key convention up front — `<entity>.<qualifier>.<event>` works well — and the wildcards do the rest. With producers publishing `order.us.placed`, `order.mx.placed`, and `order.us.payment.failed`:

| Binding key | Matches |
|---|---|
| `order.us.placed` | that one routing key, nothing else |
| `order.*.placed` | `order.us.placed` and `order.mx.placed`, but **not** `order.us.payment.failed` |
| `order.us.#` | every key under `order.us`, including `order.us.payment.failed` |
| `#` | everything published to the exchange |

```ts
await channel.assertExchange('orders', 'topic', { durable: true });

// Billing only cares about new orders, in any region.
await channel.bindQueue('orders.billing', 'orders', 'order.*.placed');

// The audit log wants the whole stream and will sort it out itself.
await channel.bindQueue('orders.audit', 'orders', '#');
```

A topic exchange with a `#` binding behaves exactly like a fanout, so a reasonable default is to make every exchange a topic exchange from day one and let the bindings decide. You give up nothing except a rounding error of routing cost, and you keep the ability to narrow a consumer down later without recreating the exchange.

(The fourth type, `headers`, matches on message headers instead of the routing key. It exists for the case where routing depends on several independent attributes that do not compose into one string. That is rare; reach for it when you actually hit it.)

### The default exchange, and why every tutorial looks like it is lying

Every "hello world" you have read publishes straight to a queue name:

```ts
channel.sendToQueue('orders.billing', payload, { persistent: true });
```

This does not break the rule. It is exactly equivalent to:

```ts
channel.publish('', 'orders.billing', payload, { persistent: true });
```

That empty string is the **default exchange** — a direct exchange that every queue is automatically bound to, using its own name as the binding key. There is no such thing as publishing to a queue; there is only a nameless exchange doing it on your behalf.

It is genuinely fine for a single-consumer job queue. It is also how the coupling comes back: the producer now hardcodes the consumer's queue name, which is the exact thing you brought in a broker to avoid. Use it for a background-jobs queue you own both ends of, and name a real exchange for anything resembling an event.



![Fanout, direct and topic exchanges side by side, showing how each one compares the routing key](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/9gixpze48r9t0hy69di7.jpg)

---

## The consumer, and the acknowledgement that decides everything

Routing gets the message into a queue. What happens next is decided by one call in your handler.

```ts
// src/topology.ts
import type { Channel } from 'amqplib';

export const ORDERS_EXCHANGE = 'orders';
export const RETRY_EXCHANGE = 'orders.retry';
export const DEAD_EXCHANGE = 'orders.dead';

export const BILLING_QUEUE = 'orders.billing';
export const BILLING_RETRY_QUEUE = 'orders.billing.retry';
export const BILLING_DEAD_QUEUE = 'orders.billing.dead';

export const RETRY_DELAY_MS = 30_000;

/**
 * Declaring topology is idempotent, so every process asserts the full topology on
 * startup instead of relying on whoever booted first. Keeping it in one module means
 * the producer and the consumer cannot drift into disagreeing about the bindings.
 */
export async function assertTopology(channel: Channel): Promise<void> {
  await channel.assertExchange(ORDERS_EXCHANGE, 'topic', { durable: true });
  await channel.assertExchange(RETRY_EXCHANGE, 'topic', { durable: true });
  await channel.assertExchange(DEAD_EXCHANGE, 'topic', { durable: true });

  await channel.assertQueue(BILLING_QUEUE, {
    durable: true,
    arguments: {
      // Quorum queues are the recommended default in RabbitMQ 4.x: replicated by
      // Raft, and they cap redeliveries themselves (see x-delivery-limit below).
      'x-queue-type': 'quorum',
      // A message returned this many times by consumers that died before acking is
      // dead-lettered instead of looping. On RabbitMQ 4.x an explicit nack with requeue
      // does not count. With no dead-letter exchange configured it would be dropped.
      'x-delivery-limit': 5,
      'x-dead-letter-exchange': DEAD_EXCHANGE,
    },
  });
  await channel.bindQueue(BILLING_QUEUE, ORDERS_EXCHANGE, 'order.*.placed');

  // Holds a failed message for RETRY_DELAY_MS, then dead-letters it back to `orders`.
  // Note the *absence* of x-dead-letter-routing-key: omitting it preserves the
  // original routing key, so the message re-enters exactly the queues it came from.
  await channel.assertQueue(BILLING_RETRY_QUEUE, {
    durable: true,
    arguments: {
      'x-message-ttl': RETRY_DELAY_MS,
      'x-dead-letter-exchange': ORDERS_EXCHANGE,
    },
  });
  await channel.bindQueue(BILLING_RETRY_QUEUE, RETRY_EXCHANGE, 'order.*.placed');

  // The terminal stop. Nothing consumes this queue; a human reads it.
  await channel.assertQueue(BILLING_DEAD_QUEUE, { durable: true });
  await channel.bindQueue(BILLING_DEAD_QUEUE, DEAD_EXCHANGE, '#');
}
```

Two settings in there are the ones people conflate. `durable: true` on a queue means **the queue definition** survives a broker restart. `persistent: true` on a published message means **that message** is written to disk. On a classic queue you need both: a persistent message in a non-durable queue dies with the queue, and a durable queue full of non-persistent messages comes back empty. (A quorum queue, like `orders.billing` below, writes every message to disk whatever its delivery mode, but the retry and dead-letter queues here are classic.) Neither combination raises an error, which is why this is usually discovered during an incident.

Now the consumer:

```ts
// src/billing-consumer.ts
import amqp from 'amqplib';
import type { Channel, ConsumeMessage } from 'amqplib';
import { assertTopology, BILLING_QUEUE, RETRY_EXCHANGE } from './topology.ts';

const RABBIT_URL = process.env.RABBIT_URL ?? 'amqp://aurora:aurora-dev@localhost:5672';
const PAYMENTS_URL = process.env.PAYMENTS_URL ?? 'http://localhost:4001';
const MAX_RETRIES = 3;

type OrderPlaced = {
  orderId: string;
  sku: string;
  quantity: number;
  totalCents: number;
};

const RETRY_COUNT_HEADER = 'retry-count';

/**
 * How many times this message has already been parked in the retry queue. The consumer
 * counts it itself: RabbitMQ 4.x ignores the x-death a client sends when it republishes,
 * so x-death never gets past 1 and a failing order would be retried forever.
 */
function retriesSoFar(message: ConsumeMessage): number {
  const count: unknown = message.properties.headers?.[RETRY_COUNT_HEADER];
  return typeof count === 'number' ? count : 0;
}

function parseOrder(message: ConsumeMessage): OrderPlaced {
  const parsed: unknown = JSON.parse(message.content.toString('utf8'));

  if (
    typeof parsed !== 'object' || parsed === null ||
    typeof (parsed as OrderPlaced).orderId !== 'string' ||
    typeof (parsed as OrderPlaced).totalCents !== 'number'
  ) {
    throw new SyntaxError('order payload is missing orderId or totalCents');
  }

  return parsed as OrderPlaced;
}

async function chargeCard(order: OrderPlaced): Promise<string> {
  const response = await fetch(`${PAYMENTS_URL}/charges`, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      // Delivery is at-least-once, so the same order can arrive twice. The order id
      // as idempotency key means the second attempt returns the first charge instead
      // of billing the customer again.
      'idempotency-key': order.orderId,
    },
    body: JSON.stringify({ amountCents: order.totalCents, reference: order.orderId }),
    signal: AbortSignal.timeout(5_000),
  });

  if (!response.ok) {
    throw new Error(`payments service returned ${response.status} for ${order.orderId}`);
  }

  const { chargeId } = (await response.json()) as { chargeId: string };
  return chargeId;
}

async function handle(channel: Channel, message: ConsumeMessage): Promise<void> {
  let order: OrderPlaced;

  try {
    order = parseOrder(message);
  } catch (error) {
    // Malformed payload. Retrying cannot fix a message that will never parse, so it
    // goes straight to the dead-letter queue on the first attempt.
    console.error('[billing] unparseable message, dead-lettering', error);
    channel.nack(message, false, false);
    return;
  }

  try {
    const chargeId = await chargeCard(order);
    console.log(`[billing] charged ${order.orderId} -> ${chargeId}`);
    channel.ack(message);
    return;
  } catch (error) {
    const attempts = retriesSoFar(message);

    if (attempts >= MAX_RETRIES) {
      console.error(`[billing] giving up on ${order.orderId} after ${attempts} retries`, error);
      channel.nack(message, false, false); // -> DEAD_EXCHANGE -> orders.billing.dead
      return;
    }

    console.warn(`[billing] retry ${attempts + 1}/${MAX_RETRIES} for ${order.orderId}`, error);

    // Park it in the delay queue with its original routing key, and count the attempt
    // in a header of our own: the broker does not carry x-death across a republish.
    channel.publish(RETRY_EXCHANGE, message.fields.routingKey, message.content, {
      persistent: true,
      headers: { ...message.properties.headers, [RETRY_COUNT_HEADER]: attempts + 1 },
      messageId: message.properties.messageId,
    });
    channel.ack(message);
  }
}

const connection = await amqp.connect(RABBIT_URL);
const channel = await connection.createChannel();
await assertTopology(channel);

// Without a prefetch, RabbitMQ pushes the entire queue at the first consumer that
// connects and every other replica sits idle. A modest window keeps consumers evenly
// loaded while still pipelining.
await channel.prefetch(20);

await channel.consume(BILLING_QUEUE, (message) => {
  // null arrives when the consumer is cancelled server-side; there is nothing to ack.
  if (message === null) return;
  void handle(channel, message);
});

console.log(`[billing] consuming ${BILLING_QUEUE}`);

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.once(signal, () => {
    // Closing the channel requeues anything still unacknowledged, so a rolling deploy
    // hands in-flight work to the next pod instead of losing it.
    void channel
      .close()
      .then(() => connection.close())
      .then(() => process.exit(0));
  });
}
```

The acknowledgement is the entire contract, and it has exactly three moves:

- **`ack(message)`** — done, delete it. Only after the work actually succeeded.
- **`nack(message, false, false)`** — failed, do not requeue. Goes to the dead-letter exchange if one is configured, and is dropped if not.
- **`nack(message, false, true)`** — failed, put it back. This is the one that will hurt you.

Notice that nothing acks before doing the work. Acknowledge first and you have converted "the consumer crashed" into "the order silently never shipped".

---

## Dead letters and poison pills

A **poison pill** is a message that fails every single time — a payload your parser rejects, a reference to a row someone deleted, a bug on one specific code path. It is not a transient failure and no amount of patience fixes it.

Now combine that with `nack(message, false, true)`. The message goes back on the queue, is redelivered immediately, fails immediately, goes back on the queue. There is no delay anywhere in that loop. You are not retrying once a second, you are retrying as fast as the network allows, with your logs filling at the same rate — one message, pinning a CPU, failing very efficiently. It is the classic 3am incident, and the fix is structural: **never requeue blindly.**

Three layers stop it, and the topology above uses all three:

**1. `x-delivery-limit` on the queue.** Quorum queues count redeliveries themselves. Past the limit the broker dead-letters the message without involving your code at all — which matters, because it covers the case your handler cannot: a consumer that crashes mid-message and never reaches a `nack`. Know what it counts, though. On RabbitMQ 4.3, a message returned because its channel or connection closed counts towards the limit; an explicit `nack(message, false, true)` on a live channel does not. I measured 50 back-to-back requeues of one message with no dead-lettering at all. The limit is a safety net for crashes, not a brake on a requeue loop, which is one more reason never to requeue blindly. RabbitMQ 4.x applies a default limit to quorum queues; setting it explicitly documents your intent.

**2. A dead-letter exchange.** `x-dead-letter-exchange` names where rejected, expired, and over-limit messages go. It is an exchange, not a queue — the same routing rules apply, which is why `orders.billing.dead` is bound with `#` to catch everything regardless of routing key. Leave the DLX off and every failure is discarded silently, which is a very quiet way to lose money.

**3. A counted, delayed retry.** For genuinely transient failures — the payments service is redeploying — retrying is right, but not instantly and not forever. The consumer republishes to `RETRY_EXCHANGE`, the retry queue holds the message for its `x-message-ttl`, and expiry dead-letters it back to `orders` with the original routing key intact. On each trip the consumer increments a `retry-count` header of its own, `retriesSoFar` reads it back, and after `MAX_RETRIES` the message goes to the terminal queue. (The first version of this article read the broker's `x-death` count instead. RabbitMQ 4.x does not carry that header across a republish, so it never got past 1.)

One honest caveat on that third layer: republish-then-ack is two operations, not one transaction. Crash between them and the message is delivered twice. That is why `chargeCard` sends an idempotency key — under at-least-once delivery, handlers have to tolerate seeing the same message again. Closing the equivalent gap on the *publishing* side is what publisher confirms are for, and that is [Part 2](https://dev.to/jgomezdev/rabbitmq-from-fundamentals-to-production-part-2-building-a-scalable-service-4nk2).



![A failed message circling through a delay queue with a retry counter, exiting to a dead-letter queue once the delivery limit is hit](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/0suwgzf6hz6ck1qfw9ap.jpg)

---

## Two gotchas worth knowing before production, not during

**Unroutable messages vanish without a sound.** Publish to an exchange whose bindings match nothing and RabbitMQ discards the message with the serene confidence of a system that did precisely what you asked. `channel.publish` still returns `true`. Ask to be told about it instead:

```ts
import { once } from 'node:events';

channel.on('return', (message) => {
  console.error(
    `[publisher] unroutable: ${message.fields.routingKey} on ${message.fields.exchange}`,
  );
});

const accepted = channel.publish(ORDERS_EXCHANGE, 'order.us.placed', body, {
  persistent: true,
  contentType: 'application/json',
  messageId: order.orderId,
  mandatory: true, // no matching binding -> `return` event instead of a silent drop
});

if (!accepted) {
  // The socket write buffer is full. Publishing anyway grows an unbounded queue
  // inside your own process, which is the failure a broker was supposed to prevent.
  await once(channel, 'drain');
}
```

A typo in a routing key is otherwise indistinguishable from "no consumer cares", and the alternative is finding out from the finance team.

**You cannot change a queue's arguments in place.** `assertQueue` is idempotent only while the arguments match. Assert an existing queue with a different `x-dead-letter-exchange` — exactly what happens the day you add dead-lettering to a live service — and the broker replies `PRECONDITION_FAILED` and closes the channel. The queue is unharmed; your process is not. Migrating means declaring a new queue, binding it alongside the old one, draining the original, and then removing it. The exception is the set of arguments a **policy** can also set, which includes the dead-letter exchange, the delivery limit and the message TTL: `rabbitmqctl set_policy` applies them to an existing queue with no redeclare. Arguments with no policy equivalent, such as the queue type or single active consumer, still mean a migration. Decide your dead-letter topology before there are messages in flight, because retrofitting it is either a policy you have to remember exists or a deploy with steps.

---

## When to use a queue — and when not to

**Reach for one when:**

- The work can finish *later* without the caller waiting — invoices, receipts, thumbnails, syncing to a third party.
- Several independent consumers need the same event, and you want to add a fourth without redeploying the producer.
- Traffic is spiky and the downstream is not. A queue converts a thundering herd into a backlog that drains.
- Two services deploy on different schedules and neither should be able to take the other down.

**Leave it alone when:**

- **The caller needs the answer now.** Checkout has to know whether the card was declined. That is a synchronous call — HTTP or gRPC — and dressing it up as request/reply over a broker buys you the latency of an extra hop plus a broker to operate.
- **You need strict global ordering across many consumers.** RabbitMQ preserves order within a queue delivered to a single consumer, and the moment you scale to two consumers for throughput, "in order" is over. If ordering per key is a hard requirement, that is a partitioned-log shape — Kafka, or RabbitMQ streams.
- **You need to replay history.** A queue deletes messages once they are acknowledged. Reprocessing last Tuesday means an event log, not a queue.
- **You just want one function to stop blocking a request.** That is what `await` and a background task are for. A broker is infrastructure you now have to monitor, secure, upgrade, and explain to whoever is on call.

**Rule of thumb: a queue earns its operational cost when a message must outlive the process that produced it. If it only has to outlive the request, you have cheaper options.**

---

## Key Takeaways

- **Producers publish to exchanges, never to queues.** `sendToQueue` is the nameless default exchange doing it for you, and it quietly hardcodes the consumer's queue name into the producer.
- **The routing key comes from the message; the binding key comes from the binding.** The exchange type decides how the two are compared — exact for `direct`, ignored for `fanout`, pattern-matched for `topic`.
- **Default to `topic`.** A `#` binding makes it behave like a fanout, so you keep the option to narrow a consumer later without recreating the exchange.
- **`durable` and `persistent` are two different settings** — one keeps the queue across a restart, the other keeps the messages. Setting one without the other loses data and raises no error.
- **Never requeue blindly.** `nack(msg, false, true)` on a poison pill is an unthrottled hot loop. A dead-letter exchange, a delivery limit, and a counted delay-retry are the three layers that prevent it, and they cost about fifteen lines to declare.

---

**Next in the series:** [*RabbitMQ from Fundamentals to Production, Part 2: Building a Scalable Service*](https://dev.to/jgomezdev/rabbitmq-from-fundamentals-to-production-part-2-building-a-scalable-service-4nk2) — publisher confirms, connection and channel lifecycle, consumer scaling, and what actually happens to all of this when the broker restarts under load.
