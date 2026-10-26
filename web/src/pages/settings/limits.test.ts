import { describe, expect, it } from 'vitest';
import { validateLimits } from './limits';

describe('validateLimits', () => {
  it('any non-negative count is fine', () =>
    expect(validateLimits([{ year: 2022, maxInstances: 4 }, { year: 2024, maxInstances: 3 }])).toBeNull());

  it('negative is an error', () => expect(validateLimits([{ year: 2022, maxInstances: -1 }])).not.toBeNull());

  it('zero everywhere is allowed (agent takes nothing)', () => expect(validateLimits([{ year: 2022, maxInstances: 0 }])).toBeNull());
});
