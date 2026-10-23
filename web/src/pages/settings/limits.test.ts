import { describe, expect, it } from 'vitest';
import { validateLimits } from './limits';

describe('validateLimits', () => {
  it('within three is fine', () =>
    expect(validateLimits([{ year: 2022, maxInstances: 1 }, { year: 2024, maxInstances: 2 }])).toBeNull());

  it('more than three per agent', () =>
    expect(validateLimits([{ year: 2022, maxInstances: 2 }, { year: 2024, maxInstances: 2 }])).toBe('Не больше 3 экземпляров Revit на агента'));

  it('negative is an error', () => expect(validateLimits([{ year: 2022, maxInstances: -1 }])).not.toBeNull());

  it('zero everywhere is allowed (agent takes nothing)', () => expect(validateLimits([{ year: 2022, maxInstances: 0 }])).toBeNull());
});
