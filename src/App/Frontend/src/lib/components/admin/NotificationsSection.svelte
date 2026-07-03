<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Checkbox, Input, Label, Select, Spinner, Textarea } from 'flowbite-svelte';
  import {
    ApiKeyOutline,
    BellOutline,
    BellRingOutline,
    CheckCircleOutline,
    EditOutline,
    ExclamationCircleOutline,
    FlaskOutline,
    LockOutline,
    PaperPlaneOutline,
    PlusOutline,
    RefreshOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    NOTIFICATION_PROVIDER_KEY_PATTERN,
    NOTIFICATION_PROVIDER_KINDS,
    NOTIFICATION_SECRET_NAME_PATTERN,
    deleteNotificationProvider,
    deleteNotificationProviderSecret,
    getNotificationProvider,
    listNotificationProviders,
    sendTestNotification,
    upsertNotificationProvider,
    upsertNotificationProviderSecrets,
    type NotificationProvider
  } from '$lib/api/notifications';

  interface SelectItem {
    value: string;
    name: string;
  }

  type FieldType = 'text' | 'password' | 'number' | 'checkbox' | 'textarea';

  interface ConfigField {
    key: string;
    label: string;
    type?: FieldType;
    placeholder?: string;
    secret?: boolean;
  }

  interface ServiceOption {
    value: string;
    name: string;
    configKey: string;
    fields: ConfigField[];
  }

  interface ProviderFormDefinition {
    name: string;
    fields?: ConfigField[];
    serviceLabel?: string;
    serviceConfigKey?: string;
    services?: ServiceOption[];
  }

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const checkboxClass = 'text-sm text-slate-300';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800! disabled:opacity-60';
  const rowActionClass =
    'inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-50';

  let providers = $state<NotificationProvider[]>([]);
  let selectedProviderKey = $state('');
  let loading = $state(true);
  let loadingProviderKey = $state<string | null>(null);
  let loadError = $state<string | null>(null);
  let mutationError = $state<string | null>(null);
  let statusMessage = $state<string | null>(null);

  let editorMode = $state<'create' | 'update'>('create');
  let providerKey = $state('');
  let providerKind = $state('email');
  let providerEnabled = $state(true);
  let displayName = $state('');
  let defaultTo = $state('');
  let serviceProvider = $state('');
  let configValues = $state<Record<string, string | boolean>>({});
  let secretValues = $state<Record<string, string>>({});
  let preservedSecretRefs = $state<Record<string, string>>({});
  let savingProvider = $state(false);

  let secretName = $state('');
  let secretValue = $state('');
  let savingSecret = $state(false);
  let deletingSecret = $state(false);

  let testSubject = $state('FrostStream notification test');
  let testBody = $state('This is a test notification from FrostStream.');
  let testingProvider = $state(false);

  let deleteModalOpen = $state(false);
  let deleteTarget = $state<NotificationProvider | null>(null);
  let deletingProviderKey = $state<string | null>(null);

  const selectedProvider = $derived(
    providers.find((provider) => provider.providerKey === selectedProviderKey) ?? null
  );
  const activeDefinition = $derived(providerDefinition(providerKind));
  const activeService = $derived(activeDefinition.services?.find((service) => service.value === serviceProvider) ?? null);
  const activeFields = $derived(activeService?.fields ?? activeDefinition.fields ?? []);

  const providerDefinitions: Record<string, ProviderFormDefinition> = {
    email: {
      name: 'Email',
      serviceLabel: 'Email service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'smtp',
          name: 'SMTP',
          configKey: 'smtp',
          fields: [
            { key: 'host', label: 'SMTP host', placeholder: 'smtp.example.com' },
            { key: 'port', label: 'Port', type: 'number', placeholder: '587' },
            { key: 'useSsl', label: 'Use SSL/TLS', type: 'checkbox' },
            { key: 'username', label: 'Username' },
            { key: 'password', label: 'Password', type: 'password', secret: true },
            { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        },
        { value: 'sendGrid', name: 'SendGrid', configKey: 'sendGrid', fields: emailApiFields() },
        { value: 'postmark', name: 'Postmark', configKey: 'postmark', fields: emailApiFields() },
        { value: 'resend', name: 'Resend', configKey: 'resend', fields: emailApiFields() },
        {
          value: 'mailgun',
          name: 'Mailgun',
          configKey: 'mailgun',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'domain', label: 'Domain', placeholder: 'mg.example.com' },
            { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        },
        {
          value: 'awsSes',
          name: 'AWS SES',
          configKey: 'awsSes',
          fields: [
            { key: 'region', label: 'Region', placeholder: 'us-east-1' },
            { key: 'accessKey', label: 'Access key', type: 'password', secret: true },
            { key: 'secretKey', label: 'Secret key', type: 'password', secret: true },
            { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        },
        {
          value: 'azureCommEmail',
          name: 'Azure Communication Email',
          configKey: 'azureCommEmail',
          fields: [
            { key: 'connectionString', label: 'Connection string', type: 'password', secret: true },
            { key: 'fromEmail', label: 'From email', placeholder: 'DoNotReply@example.azurecomm.net' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        }
      ]
    },
    sms: {
      name: 'SMS',
      serviceLabel: 'SMS service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'twilio',
          name: 'Twilio',
          configKey: 'twilio',
          fields: [
            { key: 'accountSid', label: 'Account SID' },
            { key: 'authToken', label: 'Auth token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'vonage',
          name: 'Vonage',
          configKey: 'vonage',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'apiSecret', label: 'API secret', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'sinch',
          name: 'Sinch',
          configKey: 'sinch',
          fields: [
            { key: 'servicePlanId', label: 'Service plan ID' },
            { key: 'apiToken', label: 'API token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'messageBird',
          name: 'MessageBird',
          configKey: 'messageBird',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'originator', label: 'Originator', placeholder: 'FrostStream' }
          ]
        },
        {
          value: 'plivo',
          name: 'Plivo',
          configKey: 'plivo',
          fields: [
            { key: 'authId', label: 'Auth ID' },
            { key: 'authToken', label: 'Auth token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'awsSns',
          name: 'AWS SNS',
          configKey: 'awsSns',
          fields: [
            { key: 'region', label: 'Region', placeholder: 'us-east-1' },
            { key: 'accessKey', label: 'Access key', type: 'password', secret: true },
            { key: 'secretKey', label: 'Secret key', type: 'password', secret: true },
            { key: 'senderId', label: 'Sender ID', placeholder: 'FROST' },
            { key: 'smsType', label: 'SMS type', placeholder: 'Transactional' }
          ]
        },
        {
          value: 'azureCommSms',
          name: 'Azure Communication SMS',
          configKey: 'azureCommSms',
          fields: [
            { key: 'connectionString', label: 'Connection string', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'msg91',
          name: 'MSG91',
          configKey: 'msg91',
          fields: [
            { key: 'authKey', label: 'Auth key', type: 'password', secret: true },
            { key: 'senderId', label: 'Sender ID' },
            { key: 'route', label: 'Route' }
          ]
        }
      ]
    },
    push: {
      name: 'Push',
      serviceLabel: 'Push service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'oneSignal',
          name: 'OneSignal',
          configKey: 'oneSignal',
          fields: [
            { key: 'appId', label: 'App ID' },
            { key: 'apiKey', label: 'API key', type: 'password', secret: true }
          ]
        },
        {
          value: 'fcm',
          name: 'Firebase Cloud Messaging',
          configKey: 'fcm',
          fields: [
            { key: 'projectId', label: 'Project ID' },
            { key: 'serviceAccountJson', label: 'Service account JSON', type: 'textarea', secret: true }
          ]
        },
        {
          value: 'expo',
          name: 'Expo',
          configKey: 'expo',
          fields: [{ key: 'accessToken', label: 'Access token', type: 'password', secret: true }]
        },
        {
          value: 'apns',
          name: 'Apple Push Notification service',
          configKey: 'apns',
          fields: [
            { key: 'bundleId', label: 'Bundle ID' },
            { key: 'teamId', label: 'Team ID' },
            { key: 'keyId', label: 'Key ID' },
            { key: 'privateKey', label: 'Private key', type: 'textarea', secret: true }
          ]
        }
      ]
    },
    whatsapp: {
      name: 'WhatsApp',
      serviceLabel: 'WhatsApp service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'twilio',
          name: 'Twilio',
          configKey: 'twilio',
          fields: [
            { key: 'accountSid', label: 'Account SID' },
            { key: 'authToken', label: 'Auth token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: 'whatsapp:+15551234567' }
          ]
        },
        {
          value: 'metaCloud',
          name: 'Meta Cloud',
          configKey: 'metaCloud',
          fields: [
            { key: 'accessToken', label: 'Access token', type: 'password', secret: true },
            { key: 'phoneNumberId', label: 'Phone number ID' }
          ]
        },
        {
          value: 'vonage',
          name: 'Vonage',
          configKey: 'vonage',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'apiSecret', label: 'API secret', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'msg91',
          name: 'MSG91',
          configKey: 'msg91',
          fields: [
            { key: 'authKey', label: 'Auth key', type: 'password', secret: true },
            { key: 'integratedNumber', label: 'Integrated number' },
            { key: 'namespace', label: 'Namespace' }
          ]
        }
      ]
    },
    slack: {
      name: 'Slack',
      fields: [
        { key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true },
        { key: 'botToken', label: 'Bot token', type: 'password', secret: true }
      ]
    },
    discord: { name: 'Discord', fields: [{ key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true }] },
    teams: { name: 'Microsoft Teams', fields: [{ key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true }] },
    telegram: {
      name: 'Telegram',
      fields: [
        { key: 'botToken', label: 'Bot token', type: 'password', secret: true },
        { key: 'chatId', label: 'Chat ID' },
        { key: 'parseMode', label: 'Parse mode', placeholder: 'Markdown' }
      ]
    },
    facebook: { name: 'Facebook', fields: [{ key: 'pageAccessToken', label: 'Page access token', type: 'password', secret: true }] },
    line: { name: 'Line', fields: [{ key: 'channelAccessToken', label: 'Channel access token', type: 'password', secret: true }] },
    viber: {
      name: 'Viber',
      fields: [
        { key: 'botAuthToken', label: 'Bot auth token', type: 'password', secret: true },
        { key: 'senderName', label: 'Sender name', placeholder: 'FrostStream' },
        { key: 'senderAvatarUrl', label: 'Sender avatar URL' }
      ]
    },
    mattermost: {
      name: 'Mattermost',
      fields: [
        { key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true },
        { key: 'channel', label: 'Channel', placeholder: 'town-square' },
        { key: 'username', label: 'Username', placeholder: 'FrostStream' }
      ]
    },
    rocketchat: {
      name: 'Rocket.Chat',
      fields: [
        { key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true },
        { key: 'channel', label: 'Channel', placeholder: '#general' },
        { key: 'username', label: 'Username', placeholder: 'FrostStream' }
      ]
    }
  };

  const providerKindItems: SelectItem[] = NOTIFICATION_PROVIDER_KINDS.map((kind) => ({
    value: kind,
    name: providerDefinitions[kind]?.name ?? titleCase(kind)
  }));

  onMount(() => {
    void loadProviders();
  });

  async function loadProviders(selectKey = selectedProviderKey) {
    loading = true;
    loadError = null;
    try {
      providers = sortProviders(await listNotificationProviders());
      const nextKey = providers.some((provider) => provider.providerKey === selectKey)
        ? selectKey
        : (providers[0]?.providerKey ?? '');
      if (nextKey) {
        await selectProvider(nextKey);
      } else {
        startCreate();
      }
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load notification providers.';
    } finally {
      loading = false;
    }
  }

  async function selectProvider(key: string) {
    selectedProviderKey = key;
    loadingProviderKey = key;
    mutationError = null;
    statusMessage = null;
    try {
      const provider = await getNotificationProvider(key);
      providers = sortProviders([
        provider,
        ...providers.filter((item) => item.providerKey !== provider.providerKey)
      ]);
      applyProvider(provider);
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not load the notification provider.';
    } finally {
      loadingProviderKey = null;
    }
  }

  function startCreate() {
    editorMode = 'create';
    selectedProviderKey = '';
    providerKey = '';
    providerKind = 'email';
    providerEnabled = true;
    displayName = '';
    defaultTo = '';
    resetConfigForKind('email');
    secretName = '';
    secretValue = '';
    mutationError = null;
    statusMessage = null;
  }

  function applyProvider(provider: NotificationProvider) {
    editorMode = 'update';
    providerKey = provider.providerKey;
    providerKind = provider.providerKind;
    providerEnabled = provider.enabled;
    displayName = provider.displayName ?? '';
    defaultTo = provider.defaultTo ?? '';
    hydrateConfig(provider.providerKind, provider.notifyConfig);
    secretName = '';
    secretValue = '';
  }

  async function saveProvider(event: SubmitEvent) {
    event.preventDefault();
    mutationError = null;
    statusMessage = null;

    const key = providerKey.trim();
    if (!NOTIFICATION_PROVIDER_KEY_PATTERN.test(key)) {
      mutationError = 'Provider key must match ^[a-z0-9-]{2,100}$.';
      return;
    }

    let notifyConfig: Record<string, unknown>;
    let secrets: Record<string, string>;
    try {
      ({ notifyConfig, secrets } = buildNotifyConfig(key));
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Notification provider settings are invalid.';
      return;
    }

    savingProvider = true;
    try {
      const provider = await upsertNotificationProvider(key, {
        providerKey: key,
        providerKind,
        enabled: providerEnabled,
        displayName: displayName.trim() || null,
        defaultTo: defaultTo.trim() || null,
        notifyConfig
      });
      if (Object.keys(secrets).length > 0) {
        await upsertNotificationProviderSecrets(key, { secrets });
      }
      providers = sortProviders([
        provider,
        ...providers.filter((item) => item.providerKey !== provider.providerKey)
      ]);
      selectedProviderKey = provider.providerKey;
      applyProvider({
        ...provider,
        notifyConfig
      });
      statusMessage = 'Notification provider saved.';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not save the notification provider.';
    } finally {
      savingProvider = false;
    }
  }

  async function removeProvider(provider: NotificationProvider) {
    deletingProviderKey = provider.providerKey;
    mutationError = null;
    statusMessage = null;
    try {
      const preferences = await deleteNotificationProvider(provider.providerKey);
      providers = sortProviders(preferences.providers ?? []);
      deleteTarget = null;
      deleteModalOpen = false;
      const nextKey = providers[0]?.providerKey ?? '';
      if (nextKey) {
        await selectProvider(nextKey);
      } else {
        startCreate();
      }
      statusMessage = 'Notification provider deleted.';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not delete the notification provider.';
    } finally {
      deletingProviderKey = null;
    }
  }

  async function saveSecret(event: SubmitEvent) {
    event.preventDefault();
    mutationError = null;
    statusMessage = null;

    if (!selectedProviderKey) {
      mutationError = 'Save the provider before storing secrets.';
      return;
    }
    if (!NOTIFICATION_SECRET_NAME_PATTERN.test(secretName.trim())) {
      mutationError = 'Secret names must match ^[A-Za-z0-9_.-]{1,100}$.';
      return;
    }
    if (!secretValue) {
      mutationError = 'Secret value is required.';
      return;
    }

    savingSecret = true;
    try {
      await upsertNotificationProviderSecrets(selectedProviderKey, {
        secrets: { [secretName.trim()]: secretValue }
      });
      statusMessage = `Secret ${secretName.trim()} saved.`;
      secretValue = '';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not save the provider secret.';
    } finally {
      savingSecret = false;
    }
  }

  async function removeSecret() {
    mutationError = null;
    statusMessage = null;

    if (!selectedProviderKey) {
      mutationError = 'Select a provider before deleting secrets.';
      return;
    }
    if (!NOTIFICATION_SECRET_NAME_PATTERN.test(secretName.trim())) {
      mutationError = 'Secret name must match ^[A-Za-z0-9_.-]{1,100}$.';
      return;
    }

    deletingSecret = true;
    try {
      await deleteNotificationProviderSecret(selectedProviderKey, secretName.trim());
      statusMessage = `Secret ${secretName.trim()} deleted.`;
      secretValue = '';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not delete the provider secret.';
    } finally {
      deletingSecret = false;
    }
  }

  async function sendTest() {
    mutationError = null;
    statusMessage = null;

    if (!selectedProviderKey) {
      mutationError = 'Select a provider before sending a test notification.';
      return;
    }

    testingProvider = true;
    try {
      await sendTestNotification({
        providerKey: selectedProviderKey,
        subject: testSubject.trim() || null,
        body: testBody.trim() || null
      });
      statusMessage = 'Test notification sent.';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not send the test notification.';
    } finally {
      testingProvider = false;
    }
  }

  function onProviderKindChange() {
    resetConfigForKind(providerKind);
  }

  function onServiceProviderChange() {
    configValues = {};
    secretValues = {};
    preservedSecretRefs = {};
  }

  function updateConfigValue(fieldKey: string, value: string) {
    configValues = { ...configValues, [fieldKey]: value };
  }

  function updateSecretValue(fieldKey: string, value: string) {
    secretValues = { ...secretValues, [fieldKey]: value };
  }

  function updateCheckboxValue(fieldKey: string, value: boolean) {
    configValues = { ...configValues, [fieldKey]: value };
  }

  function inputValue(event: Event): string {
    return (event.currentTarget as HTMLInputElement | HTMLTextAreaElement | null)?.value ?? '';
  }

  function resetConfigForKind(kind: string) {
    const definition = providerDefinition(kind);
    serviceProvider = definition.services?.[0]?.value ?? '';
    configValues = defaultConfigValues(definition.fields ?? definition.services?.[0]?.fields ?? []);
    secretValues = {};
    preservedSecretRefs = {};
  }

  function hydrateConfig(kind: string, config: Record<string, unknown> | null | undefined) {
    const definition = providerDefinition(kind);
    const source = isRecord(config) ? config : {};
    serviceProvider = readString(source[definition.serviceConfigKey ?? 'provider'])
      || definition.services?.find((service) => isRecord(source[service.configKey]))?.value
      || definition.services?.[0]?.value
      || '';

    const service = definition.services?.find((item) => item.value === serviceProvider) ?? null;
    const fields = service?.fields ?? definition.fields ?? [];
    const fieldSource = service ? readRecord(source[service.configKey]) : source;
    const nextValues = defaultConfigValues(fields);
    const nextRefs: Record<string, string> = {};

    for (const field of fields) {
      const raw = fieldSource[field.key];
      if (field.secret && typeof raw === 'string' && raw.startsWith('secret://')) {
        nextRefs[field.key] = raw;
        nextValues[field.key] = '';
      } else if (field.type === 'checkbox') {
        nextValues[field.key] = raw === true;
      } else if (field.type === 'number') {
        nextValues[field.key] = raw === undefined || raw === null ? '' : String(raw);
      } else {
        nextValues[field.key] = raw === undefined || raw === null ? '' : String(raw);
      }
    }

    configValues = nextValues;
    secretValues = {};
    preservedSecretRefs = nextRefs;
  }

  function buildNotifyConfig(key: string): { notifyConfig: Record<string, unknown>; secrets: Record<string, string> } {
    const definition = providerDefinition(providerKind);
    const service = definition.services?.find((item) => item.value === serviceProvider) ?? null;
    const fields = service?.fields ?? definition.fields ?? [];
    const fieldConfig = buildFieldConfig(key, fields);
    const notifyConfig: Record<string, unknown> = {};

    if (service) {
      notifyConfig[definition.serviceConfigKey ?? 'provider'] = service.value;
      notifyConfig[service.configKey] = fieldConfig.config;
    } else {
      Object.assign(notifyConfig, fieldConfig.config);
    }

    return { notifyConfig, secrets: fieldConfig.secrets };
  }

  function buildFieldConfig(
    key: string,
    fields: ConfigField[]
  ): { config: Record<string, unknown>; secrets: Record<string, string> } {
    const config: Record<string, unknown> = {};
    const secrets: Record<string, string> = {};

    for (const field of fields) {
      if (field.type === 'checkbox') {
        config[field.key] = configValues[field.key] === true;
        continue;
      }

      if (field.secret) {
        const secretValueForField = (secretValues[field.key] ?? '').trim();
        if (secretValueForField) {
          secrets[secretNameForField(field.key)] = secretValueForField;
          config[field.key] = `secret://${key}/${secretNameForField(field.key)}`;
        } else if (preservedSecretRefs[field.key]) {
          config[field.key] = preservedSecretRefs[field.key];
        }
        continue;
      }

      const raw = configValues[field.key];
      const value = typeof raw === 'string' ? raw.trim() : raw;
      if (value === '' || value === undefined || value === null) {
        continue;
      }

      if (field.type === 'number') {
        const numericValue = Number(value);
        if (!Number.isFinite(numericValue)) {
          throw new Error(`${field.label} must be a number.`);
        }
        config[field.key] = numericValue;
      } else {
        config[field.key] = value;
      }
    }

    return { config, secrets };
  }

  function defaultConfigValues(fields: ConfigField[]): Record<string, string | boolean> {
    return Object.fromEntries(fields.map((field) => [field.key, field.type === 'checkbox' ? false : '']));
  }

  function serviceItems(definition: ProviderFormDefinition): SelectItem[] {
    return (definition.services ?? []).map((service) => ({ value: service.value, name: service.name }));
  }

  function providerDefinition(kind: string): ProviderFormDefinition {
    return providerDefinitions[kind] ?? { name: titleCase(kind), fields: [] };
  }

  function fieldInputType(field: ConfigField): string {
    if (field.type === 'number') return 'number';
    if (field.type === 'password') return 'password';
    return 'text';
  }

  function secretNameForField(fieldKey: string): string {
    return `${serviceProvider ? `${serviceProvider}.` : ''}${fieldKey}`;
  }

  function isRecord(value: unknown): value is Record<string, unknown> {
    return !!value && typeof value === 'object' && !Array.isArray(value);
  }

  function readRecord(value: unknown): Record<string, unknown> {
    return isRecord(value) ? value : {};
  }

  function readString(value: unknown): string {
    return typeof value === 'string' ? value : '';
  }

  function sortProviders(items: NotificationProvider[]): NotificationProvider[] {
    return [...items].sort((a, b) => a.providerKey.localeCompare(b.providerKey));
  }

  function providerLabel(provider: NotificationProvider): string {
    return provider.displayName?.trim() || provider.providerKey;
  }

  function providerSummary(provider: NotificationProvider): string {
    return [
      provider.providerKind,
      provider.defaultTo ? `to ${provider.defaultTo}` : null,
      provider.enabled ? 'enabled' : 'disabled'
    ].filter(Boolean).join(' · ');
  }

  function titleCase(value: string): string {
    return value
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/[-_]/g, ' ')
      .replace(/\b\w/g, (letter) => letter.toUpperCase());
  }

  function emailApiFields(): ConfigField[] {
    return [
      { key: 'apiKey', label: 'API key', type: 'password', secret: true },
      { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
      { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
    ];
  }
</script>

<section class={cardClass} aria-labelledby="notifications-title">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="notifications-title" class="text-base font-bold text-slate-100">Notifications</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Manage user-scoped notification providers, write-only provider secrets, and delivery tests.
      </p>
    </div>
    <div class="flex shrink-0 gap-2">
      <Button color="dark" class={outlineButtonClass} disabled={loading} onclick={() => void loadProviders()}>
        <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </Button>
      <Button color="dark" class={outlineButtonClass} onclick={startCreate}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        New provider
      </Button>
    </div>
  </div>

  {#if loadError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {/if}

  {#if mutationError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{mutationError}</span>
    </div>
  {/if}

  {#if statusMessage}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-emerald-900/60 bg-emerald-950/30 p-3 text-sm text-emerald-300"
      role="status"
    >
      <CheckCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{statusMessage}</span>
    </div>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else}
    <div class="mt-5 grid gap-5 2xl:grid-cols-[minmax(18rem,0.8fr)_minmax(0,1.2fr)]">
      <div class="min-w-0 rounded-xl border border-slate-800/80 bg-slate-950/20 p-3">
        {#if providers.length === 0}
          <div class="p-8 text-center">
            <BellOutline class="mx-auto h-9 w-9 text-slate-700" />
            <p class="mt-4 text-sm font-semibold text-slate-300">No notification providers yet</p>
            <p class="mt-1 text-sm text-slate-500">Create one to enable delivery through a Notify channel.</p>
          </div>
        {:else}
          <div class="space-y-2">
            {#each providers as provider (provider.providerKey)}
              {@const active = provider.providerKey === selectedProviderKey}
              <article
                class={[
                  'flex min-h-[4rem] flex-col gap-3 rounded-lg border px-3 py-3 transition sm:flex-row sm:items-center',
                  active
                    ? 'border-blue-500/50 bg-blue-500/10'
                    : 'border-slate-700/70 bg-[#151a26] hover:border-slate-600 hover:bg-slate-800/30'
                ]}
              >
                <button
                  type="button"
                  class="flex min-w-0 flex-1 items-center gap-3 text-left"
                  onclick={() => void selectProvider(provider.providerKey)}
                >
                  <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                    {#if loadingProviderKey === provider.providerKey}
                      <Spinner size="4" />
                    {:else if provider.enabled}
                      <BellRingOutline class="h-4.5 w-4.5" />
                    {:else}
                      <BellOutline class="h-4.5 w-4.5" />
                    {/if}
                  </span>
                  <span class="min-w-0">
                    <span class="flex min-w-0 flex-wrap items-center gap-2">
                      <span class="truncate text-sm font-semibold text-slate-100">{providerLabel(provider)}</span>
                      <Badge rounded color="gray" class="bg-slate-800! px-2! py-0.5! text-[10px]! text-slate-400!">
                        {provider.providerKey}
                      </Badge>
                    </span>
                    <span class="mt-0.5 block truncate text-xs text-slate-400">{providerSummary(provider)}</span>
                  </span>
                </button>

                <div class="flex shrink-0 gap-2 sm:ml-auto">
                  <button
                    type="button"
                    class={rowActionClass}
                    aria-label={`Edit notification provider ${provider.providerKey}`}
                    onclick={() => void selectProvider(provider.providerKey)}
                  >
                    <EditOutline class="h-4 w-4" />
                    Edit
                  </button>
                  <button
                    type="button"
                    class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-2.5 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                    title="Delete provider"
                    aria-label={`Delete notification provider ${provider.providerKey}`}
                    disabled={deletingProviderKey === provider.providerKey}
                    onclick={() => {
                      deleteTarget = provider;
                      deleteModalOpen = true;
                    }}
                  >
                    {#if deletingProviderKey === provider.providerKey}
                      <Spinner size="4" />
                    {:else}
                      <TrashBinOutline class="h-4 w-4" />
                    {/if}
                  </button>
                </div>
              </article>
            {/each}
          </div>
        {/if}
      </div>

      <div class="min-w-0 space-y-5">
        <form
          onsubmit={saveProvider}
          class="rounded-xl border border-slate-800/80 bg-slate-950/20 p-4"
          aria-labelledby="notification-provider-editor-title"
        >
          <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <h3 id="notification-provider-editor-title" class="text-sm font-bold text-slate-100">
                {editorMode === 'create' ? 'New provider' : 'Provider settings'}
              </h3>
              {#if selectedProvider}
                <p class="mt-1 text-xs text-slate-500">{providerSummary(selectedProvider)}</p>
              {/if}
            </div>
            <Badge rounded color="gray" class="w-fit bg-slate-800! px-2.5! py-1! text-[10px]! text-slate-400!">
              {editorMode}
            </Badge>
          </div>

          <div class="mt-4 grid gap-4 md:grid-cols-2">
            <div>
              <Label for="notification-provider-key" class="mb-2 text-sm font-medium text-slate-300">Provider key</Label>
              <Input
                id="notification-provider-key"
                required
                pattern={'[a-z0-9-]{2,100}'}
                minlength={2}
                maxlength={100}
                disabled={editorMode === 'update'}
                bind:value={providerKey}
                placeholder="alerts"
                class={inputClass}
              />
            </div>
            <div>
              <Label for="notification-provider-kind" class="mb-2 text-sm font-medium text-slate-300">Provider kind</Label>
              <Select
                id="notification-provider-kind"
                items={providerKindItems}
                bind:value={providerKind}
                onchange={onProviderKindChange}
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
              />
            </div>
          </div>

          <div class="mt-4 grid gap-4 md:grid-cols-2">
            <div>
              <Label for="notification-display-name" class="mb-2 text-sm font-medium text-slate-300">Display name</Label>
              <Input
                id="notification-display-name"
                maxlength={255}
                bind:value={displayName}
                placeholder="Operations alerts"
                class={inputClass}
              />
            </div>
            <div>
              <Label for="notification-default-to" class="mb-2 text-sm font-medium text-slate-300">Default recipient</Label>
              <Input
                id="notification-default-to"
                maxlength={512}
                bind:value={defaultTo}
                placeholder="#ops-alerts"
                class={inputClass}
              />
            </div>
          </div>

          <div class="mt-5 border-t border-slate-800/70 pt-4">
            <div class="flex items-center gap-2">
              <BellOutline class="h-4 w-4 text-blue-400" />
              <h4 class="text-sm font-bold text-slate-100">{activeDefinition.name} settings</h4>
            </div>

            {#if activeDefinition.services}
              <div class="mt-4 max-w-sm">
                <Label for="notification-service-provider" class="mb-2 text-sm font-medium text-slate-300">
                  {activeDefinition.serviceLabel ?? 'Service'}
                </Label>
                <Select
                  id="notification-service-provider"
                  items={serviceItems(activeDefinition)}
                  bind:value={serviceProvider}
                  onchange={onServiceProviderChange}
                  class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
                />
              </div>
            {/if}

            <div class="mt-4 grid gap-4 md:grid-cols-2">
              {#each activeFields as field (field.key)}
                {#if field.type === 'checkbox'}
                  <label class="flex min-h-10 items-center pt-7">
                    <Checkbox
                      checked={configValues[field.key] === true}
                      onchange={(event) => updateCheckboxValue(field.key, event.currentTarget.checked)}
                      class={checkboxClass}
                    >
                      {field.label}
                    </Checkbox>
                  </label>
                {:else if field.type === 'textarea'}
                  <div class="md:col-span-2">
                    <Label for={`notification-field-${field.key}`} class="mb-2 text-sm font-medium text-slate-300">
                      {field.label}
                    </Label>
                    <Textarea
                      id={`notification-field-${field.key}`}
                      rows={field.secret ? 4 : 5}
                      value={field.secret ? (secretValues[field.key] ?? '') : String(configValues[field.key] ?? '')}
                      oninput={(event) => {
                        if (field.secret) {
                          updateSecretValue(field.key, inputValue(event));
                        } else {
                          updateConfigValue(field.key, inputValue(event));
                        }
                      }}
                      placeholder={field.secret && preservedSecretRefs[field.key] ? 'Stored secret is already configured' : field.placeholder}
                      class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
                    />
                    {#if field.secret}
                      <p class="mt-1.5 text-xs text-slate-600">
                        {preservedSecretRefs[field.key]
                          ? `Leave blank to keep ${preservedSecretRefs[field.key]}.`
                          : `Stored as secret://${providerKey.trim() || 'provider'}/${secretNameForField(field.key)}.`}
                      </p>
                    {/if}
                  </div>
                {:else}
                  <div>
                    <Label for={`notification-field-${field.key}`} class="mb-2 text-sm font-medium text-slate-300">
                      {field.label}
                    </Label>
                    <Input
                      id={`notification-field-${field.key}`}
                      type={fieldInputType(field)}
                      value={field.secret ? (secretValues[field.key] ?? '') : String(configValues[field.key] ?? '')}
                      placeholder={field.secret && preservedSecretRefs[field.key] ? 'Stored secret is already configured' : field.placeholder}
                      class={inputClass}
                      oninput={(event) => {
                        if (field.secret) {
                          updateSecretValue(field.key, inputValue(event));
                        } else {
                          updateConfigValue(field.key, inputValue(event));
                        }
                      }}
                    />
                    {#if field.secret}
                      <p class="mt-1.5 text-xs text-slate-600">
                        {preservedSecretRefs[field.key]
                          ? `Leave blank to keep ${preservedSecretRefs[field.key]}.`
                          : `Stored as secret://${providerKey.trim() || 'provider'}/${secretNameForField(field.key)}.`}
                      </p>
                    {/if}
                  </div>
                {/if}
              {/each}
            </div>
          </div>

          <div class="mt-4 flex flex-col-reverse gap-3 border-t border-slate-800/70 pt-4 sm:flex-row sm:items-center sm:justify-between">
            <Checkbox bind:checked={providerEnabled} class="text-sm text-slate-300">Enabled</Checkbox>
            <Button
              type="submit"
              color="blue"
              disabled={savingProvider}
              class="border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
            >
              {#if savingProvider}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <CheckCircleOutline class="mr-1.5 h-4 w-4" />
              {/if}
              Save provider
            </Button>
          </div>
        </form>

        <section class="grid gap-5 xl:grid-cols-2">
          <form
            onsubmit={saveSecret}
            class="rounded-xl border border-slate-800/80 bg-slate-950/20 p-4"
            aria-labelledby="notification-secrets-title"
          >
            <h3 id="notification-secrets-title" class="flex items-center gap-2 text-sm font-bold text-slate-100">
              <LockOutline class="h-4 w-4 text-blue-400" />
              Provider secrets
            </h3>

            <div class="mt-4">
              <Label for="notification-secret-name" class="mb-2 text-sm font-medium text-slate-300">Secret name</Label>
              <Input
                id="notification-secret-name"
                maxlength={100}
                bind:value={secretName}
                placeholder="webhookUrl"
                class={inputClass}
              />
            </div>
            <div class="mt-4">
              <Label for="notification-secret-value" class="mb-2 text-sm font-medium text-slate-300">Secret value</Label>
              <Input
                id="notification-secret-value"
                type="password"
                bind:value={secretValue}
                placeholder="write-only"
                class={inputClass}
              />
            </div>

            <div class="mt-4 flex flex-col gap-2 sm:flex-row">
              <Button
                type="submit"
                color="blue"
                disabled={savingSecret || !selectedProviderKey}
                class="border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
              >
                {#if savingSecret}
                  <Spinner size="4" class="mr-1.5" />
                {:else}
                  <ApiKeyOutline class="mr-1.5 h-4 w-4" />
                {/if}
                Store secret
              </Button>
              <Button
                type="button"
                color="dark"
                disabled={deletingSecret || !selectedProviderKey}
                class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-red-200! hover:border-red-500/60! hover:bg-red-500/10! disabled:opacity-60"
                onclick={removeSecret}
              >
                {#if deletingSecret}
                  <Spinner size="4" class="mr-1.5" />
                {:else}
                  <TrashBinOutline class="mr-1.5 h-4 w-4" />
                {/if}
                Delete secret
              </Button>
            </div>
          </form>

          <section class="rounded-xl border border-slate-800/80 bg-slate-950/20 p-4" aria-labelledby="notification-test-title">
            <h3 id="notification-test-title" class="flex items-center gap-2 text-sm font-bold text-slate-100">
              <FlaskOutline class="h-4 w-4 text-blue-400" />
              Test delivery
            </h3>

            <div class="mt-4">
              <Label for="notification-test-subject" class="mb-2 text-sm font-medium text-slate-300">Subject</Label>
              <Input id="notification-test-subject" bind:value={testSubject} class={inputClass} />
            </div>
            <div class="mt-4">
              <Label for="notification-test-body" class="mb-2 text-sm font-medium text-slate-300">Body</Label>
              <Textarea
                id="notification-test-body"
                rows={5}
                bind:value={testBody}
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
              />
            </div>

            <Button
              type="button"
              color="blue"
              disabled={testingProvider || !selectedProviderKey}
              class="mt-4 border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
              onclick={sendTest}
            >
              {#if testingProvider}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <PaperPlaneOutline class="mr-1.5 h-4 w-4" />
              {/if}
              Send test
            </Button>
          </section>
        </section>
      </div>
    </div>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete notification provider?"
  message={deleteTarget
    ? `Delete provider "${providerLabel(deleteTarget)}"? Matching provider secrets will also be removed.`
    : 'Delete this notification provider?'}
  confirmLabel="Delete provider"
  onConfirm={async () => {
    if (deleteTarget) {
      await removeProvider(deleteTarget);
    }
  }}
/>
