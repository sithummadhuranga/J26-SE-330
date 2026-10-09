import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/core/widgets.dart';

void main() {
  test('initials from a patient label or a username', () {
    expect(InitialsAvatar.initialsOf('AF bed 12'), 'AF');
    expect(InitialsAvatar.initialsOf('A.F. bed 12'), 'AF');
    expect(InitialsAvatar.initialsOf('Demo Admin'), 'DA');
    expect(InitialsAvatar.initialsOf('admin.demo'), 'AD');
    expect(InitialsAvatar.initialsOf('n.silva'), 'NS');
    expect(InitialsAvatar.initialsOf('Bed 4'), 'B4');
    expect(InitialsAvatar.initialsOf('K'), 'K');
    expect(InitialsAvatar.initialsOf('  '), '?');
  });

  test('area is shown in cm² from the contract mm²', () => expect(formatArea(440), '4.40 cm²'));
}
